# OpenHFT-Lab

> **A high-performance High Frequency Trading laboratory built for education and demonstration**

OpenHFT-Lab is a realistic yet educational HFT system designed to showcase micromarket structure, low-latency data processing, and simulated order execution. It demonstrates professional-grade architecture patterns used in algorithmic trading firms and proprietary trading shops.

## 🎯 Objectives

- **Sub-millisecond latency**: Achieve tick-to-trade p99 < 800μs (p50 ~200-300μs) in replay mode
- **Lock-free architecture**: Hot path optimization with pre-allocated memory and minimal GC pressure
- **Real market data**: L2 order book from Binance WebSocket + L3 simulation mode
- **In-memory matching**: Price-time priority matching engine with configurable latencies
- **Comprehensive metrics**: Latency histograms (p50/p95/p99/p99.9), throughput, jitter, fill rates

## 🏗️ Architecture

```
Market Data Sources → Feed Handler → Ring Buffer → Order Book → Strategy Engine
                                                      ↓
Risk Controls ← Order Gateway ← Matching Engine ← Order Intents
     ↓
 Fill Events → Post-Trade Analytics & PnL Tracking
```

### Core Components

- **Feed Handler**: Normalizes Binance WebSocket L2 data and replay files
- **Order Book**: High-performance L2/L3 book with microstructural features (OFI, depth)
- **Strategy Engine**: Market making and liquidity-taking strategies
- **Risk Controls**: Pre-trade limits, kill-switches, fat-finger protection  
- **Order Gateway**: OUCH-like protocol simulation
- **Matching Engine**: In-memory price-time priority matching
- **Metrics & Telemetry**: HdrHistogram latency tracking, Prometheus export

## 🚀 Quick Start

### Prerequisites

- .NET 8.0 SDK
- Docker & Docker Compose (optional)
- Visual Studio 2022 or JetBrains Rider (recommended)

### Build and Run

1. **Clone the repository**
   ```bash
   git clone https://github.com/yourusername/OpenHFT-Lab.git
   cd OpenHFT-Lab
   ```

2. **Build the solution**
   ```bash
   dotnet build
   ```

3. **Run with real-time Binance data**
   ```bash
   dotnet run --project src/OpenHFT.UI -- --mode realtime --symbols BTCUSDT,ETHUSDT
   ```

4. **Run in deterministic replay mode**
   ```bash
   dotnet run --project src/OpenHFT.UI -- --mode replay --file data/replay_data.bin
   ```

### Docker Deployment

```bash
docker-compose up -d
```

This starts:
- HFT Engine with WebUI (http://localhost:3000)
- Prometheus metrics (http://localhost:9090) 
- Grafana dashboards (http://localhost:3001)

## 📊 Performance Targets

| Metric | Target | Notes |
|--------|--------|-------|
| Tick-to-Decision | p99 < 500μs | Feed → Strategy latency |
| Send-to-Ack | p99 < 300μs | Gateway → Matcher latency |  
| End-to-End | p99 < 800μs | Tick → Fill complete |
| Throughput | 50k+ msg/s | Single symbol replay |
| Jitter | σ < 100μs | Latency standard deviation |
| GC Pressure | ~0 allocs/msg | Hot path allocation-free |

## 🔧 Configuration

### Strategy Parameters

```json
{
  "Strategies": {
    "MarketMaking": {
      "Enabled": true,
      "Symbols": ["BTCUSDT", "ETHUSDT"],
      "BaseSpreadTicks": 2,
      "MaxPosition": 1000,
      "QuoteSizeTicks": 100,
      "OFIThreshold": 0.1
    }
  },
  "Risk": {
    "MaxOrderSize": 500,
    "MaxOrdersPerSecond": 100,
    "KillSwitchEnabled": true
  }
}
```

### Feed Configuration

```json
{
  "Binance": {
    "BaseUrl": "wss://stream.binance.com:9443/ws/",
    "Symbols": ["BTCUSDT", "ETHUSDT"],
    "DepthUpdateInterval": "100ms"
  },
  "Replay": {
    "DataPath": "data/",
    "TimeWarpEnabled": true,
    "MaxEventsPerSecond": 100000
  }
}
```

## 📈 Features

### Market Making Strategy
- Adaptive spread based on volatility and inventory
- Order Flow Imbalance (OFI) integration
- Inventory management with target positioning
- Risk-aware position sizing

### Liquidity Taking Strategy
- OFI-based market entry signals
- Micro-alpha generation from book imbalances
- Smart order routing and sizing

### Real-time Dashboard
- Live order book visualization (DOM)
- Trade tape and price charts
- Strategy PnL and performance metrics
- Latency histograms and system health

### Observability
- Prometheus metrics export
- Grafana dashboards for production monitoring
- Structured logging with contextual information
- Performance regression testing in CI/CD

## 🧪 Testing & Benchmarking

### Run Benchmarks
```bash
dotnet run --project bench/OpenHFT.Benchmarks --configuration Release
```

### Performance Tests
```bash
dotnet test tests/OpenHFT.Tests --logger trx --collect:"XPlat Code Coverage"
```

### Load Testing
```bash
# Replay with burst patterns
dotnet run --project src/OpenHFT.UI -- --mode replay --file data/burst_pattern.bin --max-rate 200000
```

## 📁 Project Structure

```
/src
  /OpenHFT.Core       # Shared models, utilities, ring buffers
  /OpenHFT.Feed       # Market data adapters (Binance, Replay)
  /OpenHFT.Book       # Order book implementation
  /OpenHFT.Strategy   # Trading strategies (MM, Liquidity Taking)
  /OpenHFT.Risk       # Risk controls and limits
  /OpenHFT.Gateway    # Order gateway (OUCH-like protocol)
  /OpenHFT.Matcher    # In-memory matching engine
  /OpenHFT.Metrics    # Latency tracking and telemetry
  /OpenHFT.UI         # React dashboard and main engine
  
/tests                # Unit and integration tests
/bench                # Performance benchmarks
/data                 # Sample replay data files
/docker               # Container definitions
/docs                 # Architecture diagrams and guides
```

## 🔍 Key Design Patterns

### Lock-Free Programming
- SPSC ring buffers for market data
- Single-threaded order book for consistency
- Memory barriers and volatile reads/writes

### Memory Management
- Pre-allocated object pools
- Struct-based event models to avoid allocations
- Unsafe code for critical performance paths

### Latency Optimization
- Tick-based integer pricing (no decimals)
- Inlined hot path methods
- Server GC with background collection
- ReadyToRun compilation

## 📚 Documentation

- [Architecture Deep Dive](docs/architecture.md)
- [Strategy Development Guide](docs/strategy-guide.md)
- [Performance Tuning](docs/performance.md)
- [Market Data Formats](docs/market-data.md)
- [Risk Management](docs/risk-management.md)

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Run tests (`dotnet test`)
4. Run benchmarks to ensure no performance regression
5. Commit changes (`git commit -m 'Add amazing feature'`)
6. Push to branch (`git push origin feature/amazing-feature`)
7. Open a Pull Request

## ⚠️ Disclaimer

This project is for **educational purposes only**. It demonstrates HFT concepts and architecture patterns but should not be used for actual trading without significant additional development, testing, and risk management. Real production HFT systems require:

- Regulatory compliance and licensing
- Proper risk management and circuit breakers
- Professional co-location and networking
- Extensive testing and certification
- Proper capitalization and risk controls

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🏷️ Tags

`high-frequency-trading` `algorithmic-trading` `market-making` `low-latency` `dotnet` `csharp` `financial-markets` `order-book` `market-microstructure` `quantitative-finance`
