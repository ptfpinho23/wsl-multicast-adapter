#include <linux/module.h>
#include <linux/netdevice.h>
#include <linux/skbuff.h>
#include <linux/printk.h>
#include <linux/etherdevice.h>

static struct net_device *mcast0_dev;


// Testing 
static int mcast0_open(struct net_device *dev)
{
    netif_start_queue(dev);
    pr_info("mcast0: device opened\n");
    return 0;
}

static int mcast0_stop(struct net_device *dev)
{
    netif_stop_queue(dev);
    pr_info("mcast0: device stopped\n");
    return 0;
}

static netdev_tx_t mcast0_xmit(struct sk_buff *skb, struct net_device *dev)
{
    pr_info("mcast0: got packet, len=%u\n", skb->len);

    dev_kfree_skb(skb);
    return NETDEV_TX_OK;
}

static const struct net_device_ops mcast0_ops = {
    .ndo_open       = mcast0_open,
    .ndo_stop       = mcast0_stop,
    .ndo_start_xmit = mcast0_xmit,
};

static void mcast0_setup(struct net_device *dev)
{
    ether_setup(dev);  // default stuff
    dev->netdev_ops = &mcast0_ops;

    dev->flags |= IFF_NOARP;
    dev->features |= NETIF_F_HW_CSUM;
    eth_hw_addr_random(dev);  // random MAC..
}
static int __init mcast0_init(void)
{
    mcast0_dev = alloc_netdev(0, "mcast0", NET_NAME_UNKNOWN, mcast0_setup);
    if (!mcast0_dev)
        return -ENOMEM;

    if (register_netdev(mcast0_dev)) {
        pr_err("mcast0: failed to register\n");
        free_netdev(mcast0_dev);
        return -ENODEV;
    }

    pr_info("mcast0: registered\n");
    return 0;
}

static void __exit mcast0_exit(void)
{
    unregister_netdev(mcast0_dev);
    free_netdev(mcast0_dev);
    pr_info("mcast0: unregistered\n");
}

module_init(mcast0_init);
module_exit(mcast0_exit);

MODULE_LICENSE("GPL");
MODULE_AUTHOR("Pedro Pinho");
MODULE_DESCRIPTION("WSL Multicast Virtual Adapter");
