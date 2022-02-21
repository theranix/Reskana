# High-performance controlled UDP client-server

Solution for real-time applications with low-latency. Key features:
- Delivery guarantee (but there is no guarantee of order): quick retransmission with low overhead. It works fine with high packet loss.
- Custom fragmentation: each part of packet will be tracked.
- Multithreaded session-based approach: creating connections, included simple app-level ddos protection
- Additional checksum, end-to-end encryption
- QUIC-like approach
- thread safety

# work in progress

First test: 5000 byte packets

| Packet loss | Avg Ping TCP | Avg Ping here  |
| ------------| -------------|----------------|
| 0           | 25           | 22             |
| 10          | 62           | 27             |
| 20          | 90           | 36             |
| 30          | 170          | 48             |
| 40          | 352          | 55             |
| 50          | disconnect   | 75             |
| 75          | disconnect   | 155            |
