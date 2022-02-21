# High-performance controlled UDP client-server

Solution for real-time applications with low-latency. Key features:
- Delivery guarantee (but there is guarantee of order): quick retransmission with low overhead. It works fine with high packet loss.
- Custom fragmentation: each part of packet will be tracked.
- Session-based approach: creating connections, included simple app-level ddos protection
- Additional checksum, end-to-end encryption
- QUIC like approach

# work in progress

First test: 5000 bytes packets


Packet loss | Avg Ping TCP | Avg Ping here
--------------------------------------------
0           | 25           | 22
10          | 45           | 27
20          | 62           | 36
30          | 90           | 48
40          | 170          | 55
50          | disconnect   | 75
75          | disconnect   | 155
