# Experimental WSL Multicast Adapter

This is currently a WIP! 

Experimental support for Multicast on WSL - both outbound & inbound via a virtual Linux adapter & Windows proxy service

---

## Overview

WSL2 runs Linux behind it's own networking stack - WinNAT - which drops most of inbound multicast/IGMP packets.
This breaks protocols like **DDS, mDNS, IPTV**, etc.


This projects is an experimentation around attempting to restore multicast support by:
- **`mcast0`** → a Linux kernel module providing a virtual multicast adapter inside WSL2.  
- **`WslMcastSvc`** → a Windows service that proxies multicast groups and packets to the host network.

---


## History & Changelog

- Attempted to match GUIDs between vsocks & hvsocks which didn't seem to work
- named pipes / tcp sound like viable alternatives, both with pros& cons.

---
## Features (planned)
TBD

---
