# High-performance controlled UDP client-server

This is solution for real-time applications with low-latency. 

Key features:
- Delivery guarantee (but there is no guarantee of order): quick retransmission with low overhead.
- Works fine with high packet loss.
- Custom fragmentation: each part of packet is tracked.
- Multithreaded session-based approach.
- Included simple app-level ddos protection
- Additional checksum, end-to-end encryption
- QUIC-like approach
- Thread safety for all public methods

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
