Machine: Apple M4, 10 logical cores, macOS 26.4.1, .NET 10.0.14, Release build.
Randomized strategies: 50 seeds each, at most 20,000 iterations per seed. DFS is deterministic (one run, same cap). Naive stress: 5 trials, each up to 20,000 real runs on the thread pool or 5 s.

**Iterations (or runs) to first bug**: success rate, then mean / median over the successful trials.

| Sample (bug) | Random walk | PCT d=2 | PCT d=3 | DFS | DFS pb=2 | Naive stress |
|---|---|---|---|---|---|---|
| `bank-transfer` (AssertionFailure) | 50/50 · 1.8 / 2 | 50/50 · 5.3 / 4 | 50/50 · 3.6 / 3 | 1/1 · 22 | 1/1 · 16 | 5/5 · 5.0 / 4 |
| `async-cache` (AssertionFailure) | 50/50 · 9.8 / 6 | 50/50 · 10.4 / 9 | 50/50 · 10.3 / 7 | 1/1 · 2 | 1/1 · 2 | 5/5 · 8.6 / 7 |
| `leader-election` (AssertionFailure) | 50/50 · 18.6 / 12 | 50/50 · 12.6 / 8 | 50/50 · 13.4 / 12 | 0/1 (none in 20,000) | 0/1 (none in 20,000) | 5/5 · 65.4 / 41 |
| `producer-consumer` (Deadlock) | 50/50 · 1.8 / 2 | 50/50 · 1.8 / 2 | 50/50 · 1.7 / 2 | 1/1 · 54 | 1/1 · 24 | 5/5 · 1.8 / 2 |
| `lease-fencing` (AssertionFailure) | 50/50 · 552.2 / 390 | 50/50 · 8.5 / 4 | 50/50 · 7.6 / 5 | 1/1 · 87 | 1/1 · 57 | 0/5 (none in 137) |
| `checkpoint-depth3` (AssertionFailure) | 4/50 · 14,676.8 / 17,208 | 0/50 (none in 20,000) | 50/50 · 59.4 / 41 | 0/1 (none in 20,000) | 1/1 · 306 | 5/5 · 702.4 / 669 |

**Wall-clock time to first bug**: mean over successful trials (ms). For failed trials, the time spent before giving up is shown in parentheses.

| Sample (bug) | Random walk | PCT d=2 | PCT d=3 | DFS | DFS pb=2 | Naive stress |
|---|---|---|---|---|---|---|
| `bank-transfer` (AssertionFailure) | 0.1 | 0.1 | 0.1 | 1.0 | 0.4 | 0.4 |
| `async-cache` (AssertionFailure) | 0.1 | 0.1 | 0.1 | 0.0 | 0.0 | 0.2 |
| `leader-election` (AssertionFailure) | 11.3 | 21.2 | 2.6 | – (551 ms ×1 failed) | – (432 ms ×1 failed) | 1,781.9 |
| `producer-consumer` (Deadlock) | 0.1 | 0.0 | 0.0 | 1.8 | 0.7 | 252.6 |
| `lease-fencing` (AssertionFailure) | 3.1 | 0.1 | 0.1 | 0.6 | 0.4 | – (5,025 ms ×5 failed) |
| `checkpoint-depth3` (AssertionFailure) | 160.7 (204 ms ×46 failed) | – (167 ms ×50 failed) | 0.7 | – (224 ms ×1 failed) | 1.9 | 12.7 |

**Scaling study, `checkpoint-depth3`**: N cells per transaction (the checkpoint scan is 2N steps). Mean iterations to first bug over 20 seeds (cap 20,000); naive stress: 5 trials of up to 2 s.

| N | Random walk | PCT d=3 | DFS pb=2 | Naive stress |
|---|---|---|---|---|
| 2 | 20/20 · 214.5 / 161 | 20/20 · 175.8 / 141 | 1/1 · 82 | 5/5 · 3,797.2 / 3,753 |
| 3 | 20/20 · 1,982.7 / 1,432 | 20/20 · 96.0 / 86 | 1/1 · 126 | 5/5 · 632.0 / 680 |
| 4 | 20/20 · 6,840.6 / 5,495 | 20/20 · 77.5 / 92 | 1/1 · 178 | 5/5 · 152.6 / 62 |
| 6 | 3/20 · 16,322.7 / 17,208 | 20/20 · 85.3 / 75 | 1/1 · 306 | 5/5 · 54.8 / 26 |
| 8 | 0/20 (none in 20,000) | 20/20 · 60.9 / 45 | 1/1 · 466 | 5/5 · 196.0 / 86 |
| 12 | 0/20 (none in 20,000) | 20/20 · 53.8 / 41 | 1/1 · 882 | 5/5 · 178.4 / 168 |

**Throughput** (fixed versions, random walk, 5,000 iterations each, single scheduler thread):

| Sample | Steps / iteration | Iterations / s | Scheduling steps / s |
|---|---|---|---|
| `bank-transfer` | 23.9 | 109,276 | 2,614,369 |
| `async-cache` | 10.0 | 250,860 | 2,508,605 |
| `leader-election` | 397.0 | 7,516 | 2,983,788 |
| `producer-consumer` | 58.0 | 46,442 | 2,695,410 |
| `lease-fencing` | 21.0 | 235,189 | 4,934,124 |
| `checkpoint-depth3` | 44.7 | 154,523 | 6,907,434 |
