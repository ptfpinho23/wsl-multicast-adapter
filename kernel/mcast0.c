#include <linux/module.h>
#include <linux/netdevice.h>
#include <linux/etherdevice.h>
#include <linux/skbuff.h>
#include <linux/net.h>
#include <net/sock.h>
#include <linux/kthread.h>
#include <linux/delay.h>
#include <linux/in.h>      /* sockaddr_in, htons, htonl */
#include <linux/inet.h>    /* in4_pton / in_aton helpers */

#define DRV_NAME "mcast0_kernsock"

/* Module parameters: Windows host gateway IP and TCP port */
static char *host = "172.22.112.1";   /* set to your `default via` gateway */
module_param(host, charp, 0444);
MODULE_PARM_DESC(host, "Windows host gateway IPv4 address (e.g., 172.22.112.1)");

static ushort port = 5000;
module_param(port, ushort, 0444);
MODULE_PARM_DESC(port, "TCP port on Windows listener");

static struct net_device *mcast0_dev;
static struct socket *tcp_sock;
static struct task_struct *rx_thread;

/* net_device open/stop */
static int mcast0_open(struct net_device *dev)
{
    netif_start_queue(dev);
    return 0;
}
static int mcast0_stop(struct net_device *dev)
{
    netif_stop_queue(dev);
    return 0;
}

/* TX path: send [2-byte length][frame] over TCP */
static netdev_tx_t mcast0_xmit(struct sk_buff *skb, struct net_device *dev)
{
    struct msghdr msg = {0};
    struct kvec iov[2];
    unsigned short l16 = skb->len;
    int rc;

    iov[0].iov_base = &l16;         /* length prefix */
    iov[0].iov_len  = sizeof(l16);
    iov[1].iov_base = skb->data;    /* frame bytes */
    iov[1].iov_len  = skb->len;

    msg.msg_flags = MSG_DONTWAIT;

    rc = kernel_sendmsg(tcp_sock, &msg, iov, 2, sizeof(l16) + skb->len);
    if (rc < 0)
        pr_err(DRV_NAME ": sendmsg failed rc=%d\n", rc);

    dev_kfree_skb(skb);
    return NETDEV_TX_OK;
}

static const struct net_device_ops mcast0_ops = {
    .ndo_open       = mcast0_open,
    .ndo_stop       = mcast0_stop,
    .ndo_start_xmit = mcast0_xmit,
};

/* Setup an Ethernet-like NIC */
static void mcast0_setup(struct net_device *dev)
{
    ether_setup(dev);
    dev->netdev_ops = &mcast0_ops;
    dev->flags |= IFF_NOARP;
    eth_hw_addr_random(dev);
}

/* RX thread: read [len][frame] from TCP and inject into kernel stack */
static int rxfn(void *arg)
{
    struct msghdr msg = {0};
    struct kvec iov;
    char buf[2048];

    allow_signal(SIGKILL);

    while (!kthread_should_stop()) {
        int len;

        iov.iov_base = buf;
        iov.iov_len  = sizeof(buf);

        /* blocking read until data is available */
        len = kernel_recvmsg(tcp_sock, &msg, &iov, 1, sizeof(buf), 0);
        if (len < 0) {
            if (len == -EINTR)
                continue; /* interrupted by signal; retry */
            pr_err(DRV_NAME ": recvmsg error rc=%d\n", len);
            msleep(100);
            continue;
        }

        if (len > 2) {
            u16 framelen;
            memcpy(&framelen, buf, 2);
            if (framelen > 0 && framelen <= len - 2) {
                struct sk_buff *skb = alloc_skb(framelen + NET_IP_ALIGN, GFP_KERNEL);
                if (skb) {
                    skb_reserve(skb, NET_IP_ALIGN);
                    memcpy(skb_put(skb, framelen), buf + 2, framelen);
                    skb->dev = mcast0_dev;
                    skb->protocol = eth_type_trans(skb, mcast0_dev);
                    skb->ip_summed = CHECKSUM_NONE;
                    netif_rx(skb);
                }
            }
        }
    }
    return 0;
}

static int __init mcast0_init(void)
{
    struct sockaddr_in sin = {0};
    __be32 addr = 0;
    int rc;

    /* Parse host IPv4 string to network-order __be32 */
#if LINUX_VERSION_CODE >= KERNEL_VERSION(4, 1, 0)
    if (!in4_pton(host, -1, (u8 *)&addr, -1, NULL)) {
        pr_err(DRV_NAME ": invalid host IPv4 '%s'\n", host);
        return -EINVAL;
    }
#else
    addr = in_aton(host);
    if (!addr) {
        pr_err(DRV_NAME ": invalid host IPv4 '%s'\n", host);
        return -EINVAL;
    }
#endif

    mcast0_dev = alloc_netdev(0, "mcast0", NET_NAME_UNKNOWN, mcast0_setup);
    if (!mcast0_dev)
        return -ENOMEM;

    rc = register_netdev(mcast0_dev);
    if (rc)
        return rc;

    /* Create kernel TCP socket */
    rc = sock_create_kern(&init_net, AF_INET, SOCK_STREAM, IPPROTO_TCP, &tcp_sock);
    if (rc < 0) {
        pr_err(DRV_NAME ": sock_create_kern TCP failed rc=%d\n", rc);
        unregister_netdev(mcast0_dev);
        free_netdev(mcast0_dev);
        return rc;
    }

    /* Connect to host:port */
    sin.sin_family      = AF_INET;
    sin.sin_addr.s_addr = addr;                 /* already network order */
    sin.sin_port        = htons(port);

    pr_info(DRV_NAME ": connecting to %pI4:%u\n", &sin.sin_addr.s_addr, port);

    rc = kernel_connect(tcp_sock, (struct sockaddr *)&sin, sizeof(sin), 0);
    if (rc < 0) {
        pr_err(DRV_NAME ": TCP connect %pI4:%u failed rc=%d\n", &sin.sin_addr.s_addr, port, rc);
        sock_release(tcp_sock);
        unregister_netdev(mcast0_dev);
        free_netdev(mcast0_dev);
        return rc;
    }

    rx_thread = kthread_run(rxfn, NULL, "mcast0_rx");
    if (IS_ERR(rx_thread)) {
        rc = PTR_ERR(rx_thread);
        sock_release(tcp_sock);
        unregister_netdev(mcast0_dev);
        free_netdev(mcast0_dev);
        return rc;
    }

    pr_info(DRV_NAME ": device up, connected to %pI4:%u\n", &sin.sin_addr.s_addr, port);
    return 0;
}

static void __exit mcast0_exit(void)
{
    if (rx_thread)
        kthread_stop(rx_thread);

    if (tcp_sock)
        sock_release(tcp_sock);

    unregister_netdev(mcast0_dev);
    free_netdev(mcast0_dev);

    pr_info(DRV_NAME ": released and exited\n");
}

module_init(mcast0_init);
module_exit(mcast0_exit);

MODULE_LICENSE("GPL");
MODULE_AUTHOR("Pedro Pinho");
MODULE_DESCRIPTION("WSL virtual NIC with kernel-level TCP bridge to Windows");
