#include <linux/module.h>
#include <linux/netdevice.h>
#include <linux/etherdevice.h>
#include <linux/skbuff.h>
#include <linux/net.h>
#include <net/sock.h>
#include <linux/kthread.h>
#include <linux/delay.h>
#include <linux/vm_sockets.h>

#define DRV_NAME "mcast0_kernsock"


// Must match Windows service GUID registration
// Registry GUID = {<port-hex>-facb-11e6-bd58-64006a7986d3}
// See: https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/make-integration-service
#define WIN_VSOCK_PORT 5000

// in vsock the host (on windows) always has cid 2 
#define WIN_VSOCK_CID VMADDR_CID_HOST

static struct net_device *mcast0_dev;
static struct socket *hv_sock;      // hyperv vsock compat
static struct task_struct *rx_thread;

// open / stop handlers for the net device created
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

// outbound TX
static netdev_tx_t mcast0_xmit(struct sk_buff *skb, struct net_device *dev)
{
    struct msghdr msg = {0};
    struct kvec iov[2];
    unsigned short l16 = skb->len;
    int rc;


    // header section - not needed for now
    iov[0].iov_base = &l16;
    iov[0].iov_len  = sizeof(l16);


    // actual payload
    iov[1].iov_base = skb->data;
    iov[1].iov_len  = skb->len;

    msg.msg_flags = MSG_DONTWAIT;

    rc = kernel_sendmsg(hv_sock, &msg, iov, 2, sizeof(l16) + skb->len);
    if (rc < 0)
        pr_err(DRV_NAME ": sendmsg through vsock failed rc=%d\n", rc);

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

// inbound rx
static int rxfn(void *arg)
{
    struct msghdr msg = {0};
    struct kvec iov;
    char buf[2048];  // can hold len+frame

    allow_signal(SIGKILL);

    while (!kthread_should_stop()) {
        int len;

        iov.iov_base = buf;
        iov.iov_len  = sizeof(buf);


        // blocks until frame received through the vsock
        len = kernel_recvmsg(hv_sock, &msg, &iov, 1, sizeof(buf), 0);

        if (len > 2) {
            u16 framelen;

            memcpy(&framelen, buf, 2);
            if (framelen > 0 && framelen <= len-2) {
                struct sk_buff *skb = alloc_skb(framelen + NET_IP_ALIGN, GFP_KERNEL);
                if (skb) {
                    skb_reserve(skb, NET_IP_ALIGN);
                    memcpy(skb_put(skb, framelen), buf+2, framelen);

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
    struct sockaddr_vm svm = {0};
    int rc;

    /* create the net device */
    mcast0_dev = alloc_netdev(0, "mcast0", NET_NAME_UNKNOWN, mcast0_setup);
    if (!mcast0_dev) return -ENOMEM;
    rc = register_netdev(mcast0_dev);
    if (rc) return rc;

    /* create kernel level vsock socket */
    rc = sock_create_kern(&init_net, AF_VSOCK, SOCK_STREAM, 0, &hv_sock);
    if (rc < 0) return rc;

    /* connect to Windows host on chosen port */
    svm.svm_family = AF_VSOCK;
    svm.svm_cid    = WIN_VSOCK_CID;   // host side
    svm.svm_port   = WIN_VSOCK_PORT;  // must match Windows Bind ServiceId mapping

    rc = kernel_connect(hv_sock, (struct sockaddr *)&svm, sizeof(svm), 0);
    if (rc < 0) {
        pr_err(DRV_NAME ": vsock connect failed rc=%d\n", rc);
        sock_release(hv_sock);
        unregister_netdev(mcast0_dev);
        free_netdev(mcast0_dev);
        return rc;
    }

    /* start RX thread */
    rx_thread = kthread_run(rxfn, NULL, "mcast0_rx");
    if (IS_ERR(rx_thread)) {
        rc = PTR_ERR(rx_thread);
        sock_release(hv_sock);
        unregister_netdev(mcast0_dev);
        free_netdev(mcast0_dev);
        return rc;
    }

    pr_info(DRV_NAME ": device up, connected to Windows CID=%u port=%u\n",
            WIN_VSOCK_CID, WIN_VSOCK_PORT);
    return 0;
}

static void __exit mcast0_exit(void)
{
    if (rx_thread)
        kthread_stop(rx_thread);

    if (hv_sock)
        sock_release(hv_sock);

    unregister_netdev(mcast0_dev);
    free_netdev(mcast0_dev);

    pr_info(DRV_NAME ": released and exited\n");
}

module_init(mcast0_init);
module_exit(mcast0_exit);

MODULE_LICENSE("GPL");
MODULE_AUTHOR("Pedro Pinho");
MODULE_DESCRIPTION("WSL virtual NIC with kernel level bridge to Windows");
