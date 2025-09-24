// mcast0_kernsock.c
#include <linux/module.h>
#include <linux/netdevice.h>
#include <linux/etherdevice.h>
#include <linux/skbuff.h>
#include <linux/in.h>
#include <linux/net.h>
#include <net/sock.h>
#include <linux/kthread.h>
#include <linux/delay.h>

#define DRV_NAME "mcast0_kernsock"

// TESTING! 
// win set to listen in on localhost:53530
#define WIN_PORT 53530

// Linux set to listen here for replies coming from win
#define LNX_PORT 53531

static struct net_device *mcast0_dev;
static struct socket *udp_sock;
static struct task_struct *rx_thread;

/* Open/stop handlers for the net_device */
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

/* Outbound: called when Linux transmits a packet via mcast0 */
static netdev_tx_t mcast0_xmit(struct sk_buff *skb, struct net_device *dev)
{
    struct msghdr msg = {0};
    struct kvec iov[2];
    unsigned short l16 = skb->len;
    int rc;

    /* Two pieces: kvec - [len][frame] */

    // header section - not needed for now
    iov[0].iov_base = &l16;
    iov[0].iov_len  = sizeof(l16);

    // actual payload 
    iov[1].iov_base = skb->data;
    iov[1].iov_len  = skb->len;

    msg.msg_flags = MSG_DONTWAIT;

    rc = kernel_sendmsg(udp_sock, &msg, iov, 2, sizeof(l16)+skb->len);
    if (rc < 0)
        pr_err(DRV_NAME ": sendmsh through socket failed rc=%d\n", rc);

    dev_kfree_skb(skb);
    return NETDEV_TX_OK;
}

static const struct net_device_ops mcast0_ops = {
    .ndo_open       = mcast0_open,
    .ndo_stop       = mcast0_stop,
    .ndo_start_xmit = mcast0_xmit,
};

/* Setup: Ethernet-like NIC with random MAC */
static void mcast0_setup(struct net_device *dev)
{
    ether_setup(dev);
    dev->netdev_ops = &mcast0_ops;
    dev->flags |= IFF_NOARP;
    eth_hw_addr_random(dev);
}

/* RX thread: receive from UDP socket and inject into Linux */
static int rxfn(void *arg)
{
    struct msghdr msg = {0};
    struct kvec iov;
    char buf[2048];

    allow_signal(SIGKILL);

    // TODO - fixme -> look into NAPI instead of direct poll
    while (!kthread_should_stop()) {
        int len;
        iov.iov_base = buf;
        iov.iov_len  = sizeof(buf);
        len = kernel_recvmsg(udp_sock, &msg, &iov, 1, sizeof(buf), MSG_DONTWAIT);

        // 
        if (len > 2) {
            u16 framelen;

            // copies the first 2 bytes of the udp payload - carries our "ethernet frame" len
            memcpy(&framelen, buf, 2);
            if (framelen > 0 && framelen <= len-2) {

                // alloc skb to hold the frame
                struct sk_buff *skb = alloc_skb(framelen+NET_IP_ALIGN, GFP_KERNEL);
                if (skb) {

                    // align memory space -> 4 byte
                    skb_reserve(skb, NET_IP_ALIGN);

                    // retrieve the actual ethernet frame (iov[1])
                    memcpy(skb_put(skb, framelen), buf+2, framelen);

                    skb->dev = mcast0_dev;

                    // set protocol looking at the ethernet frame
                    skb->protocol = eth_type_trans(skb, mcast0_dev);

                    // bypasses checksum checks within the kernel
                    skb->ip_summed = CHECKSUM_NONE;

                    // deliver packet directly onto the kernel stack
                    netif_rx_ni(skb);
                }
            }
        }
        msleep(10); // avoid spinning  - terrible, fixme
    }
    return 0;
}

static int __init mcast0_init(void)
{
    struct sockaddr_in sin = {0};
    int rc;

    // creates the net device
    mcast0_dev = alloc_netdev(0, "mcast0", NET_NAME_UNKNOWN, mcast0_setup);
    if (!mcast0_dev) return -ENOMEM;
    rc = register_netdev(mcast0_dev);
    if (rc) return rc;

    // create kernel level UDP socket
    rc = sock_create_kern(&init_net, AF_INET, SOCK_DGRAM, IPPROTO_UDP, &udp_sock);
    if (rc < 0) return rc;

    // bind udp sock on linux side
    sin.sin_family = AF_INET;
    sin.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    sin.sin_port = htons(LNX_PORT);
    kernel_bind(udp_sock, (struct sockaddr*)&sin, sizeof(sin));

    // connects to the default peer on windows side
    sin.sin_port = htons(WIN_PORT);
    kernel_connect(udp_sock, (struct sockaddr*)&sin, sizeof(sin), 0);

    // kick of RX thread 
    rx_thread = kthread_run(rxfn, NULL, "mcast0_rx");
    if (IS_ERR(rx_thread)) {
        rc = PTR_ERR(rx_thread);
        sock_release(udp_sock);
        unregister_netdev(mcast0_dev);
        free_netdev(mcast0_dev);
        return rc;
    }

    pr_info(DRV_NAME ": network device ready on %s:%d, from %d)\n",
            "127.0.0.1", WIN_PORT, LNX_PORT);
    return 0;
}

static void __exit mcast0_exit(void)
{

    // stop rx thread
    if (rx_thread) kthread_stop(rx_thread);

    // release socket allocation
    if (udp_sock) sock_release(udp_sock);

    // unregister network device
    unregister_netdev(mcast0_dev);
    free_netdev(mcast0_dev);

    pr_info(DRV_NAME ": released and exited\n");
}

module_init(mcast0_init);
module_exit(mcast0_exit);

MODULE_LICENSE("GPL");
MODULE_AUTHOR("Pedro Pinho");
MODULE_DESCRIPTION("WSL virtual NIC with kernel level bridge to Windows");
