# OpenHFT-Lab — Arquitectura y Plan de Implementación

> **Propósito**: Construir un laboratorio educativo pero realista de HFT (*High Frequency Trading*) que demuestre microestructura de mercado, procesamiento de datos de baja latencia y ejecución simulada de órdenes con métricas rigurosas. El proyecto sirve como pieza bandera para roles en brokers/prop shops.

---

## 1) Objetivos y Alcance

### Objetivos

- **Tick-to-trade** sub-milisegundo en entorno local (meta: p99 < 800 µs, p50 \~200–300 µs) bajo modo *replay*.
- Ordenar el hot path con **estructuras lock-free**, memoria pre-asignada y GC mínimo.
- Mantener un **Order Book** L2 real (desde Binance) y **L3** en modo simulado.
- **Motor de matching** in-memory (simulado) con prioridad price-time.
- **Métricas**: latencias (p50/p95/p99/p99.9), throughput, jitter, fill rate, PnL simulado.

### Alcance (MVP)

- Modo **Real-Time** (Binance WS L2) → Strategy → Risk → Gateway → Matching simulado.
- Modo **Replay determinista** (ITCH-like binario) → idem.
- UI web (React) para depth, tape, métricas y PnL.
- Observabilidad (Prometheus + Grafana) y benchmarks automatizados.

Fuera de alcance (MVP): colocation real, acceso a mercados regulados, HSM/PKI avanzados, cumplimiento normativo.

---

## 2) Arquitectura Lógica

```
┌───────────────────────┐
│  Market Data Sources  │  (Binance WS L2 | Replay ITCH-like)
└──────────┬────────────┘
           │
           ▼
┌───────────────────────┐   normalize  ┌──────────────────────┐
│     Feed Handler      ├─────────────►│  Ring Buffer (MDQ)   │  (lock-free)
└──────────┬────────────┘              └─────────┬────────────┘
           │ apply deltas                           │
           ▼                                        ▼
┌───────────────────────┐                    ┌───────────────────────┐
│      Order Book       │◄───────────────────┤  Metrics & Telemetry  │
│  (L2 real, L3 sim)    │                    │ (HdrHistogram, logs)  │
└──────────┬────────────┘                    └──────────┬────────────┘
           │ features (spread, OFI, depth)              │
           ▼                                            │
┌───────────────────────┐                                │
│     Strategy Engine   │                                │
│ (MM & Liquidity-Take) │                                │
└──────────┬────────────┘                                │
           │ orders                                      │
           ▼                                            ▼
┌───────────────────────┐      pre-trade checks   ┌──────────────────────┐
│      Order Gateway    ├────────────────────────►│   Risk / Controls    │
│ (Sim OUCH / Paper WS) │                          │(limits, kill-switch)│
└──────────┬────────────┘                          └──────────┬───────────┘
           │  send & acks                                     │
           ▼                                                  ▼
┌───────────────────────┐                              ┌───────────────────────┐
│    Matching Engine    │ (in-memory)                  │  Post-Trade Analytics │
│  (price-time priority)│                              │ (PnL, slippage, FR)  │
└───────────────────────┘                              └───────────────────────┘
```

### Flujos clave

1. **Snapshot + Deltas**: Snapshot inicial (REST) + deltas (WS) aplicados en orden (sequence id). Re-sync si hay huecos.
2. **Features microestructurales**: spread, depth por nivel, **Order Flow Imbalance (OFI)**, agresión de trade, etc.
3. **Estrategias** (MVP):
   - **Market Making** con objetivos de inventario y re-quote adaptativo.
   - **Liquidity Taking** basada en OFI + micro-alphas.
4. **Riesgo pre-trade**: límites de tasa, tamaño, inventario, *fat finger*, kill-switch.
5. **Ejecución simulada**: gateway OUCH-like hacia matcher in-memory.

---

## 3) Modos de Operación

### Modo A — Real-Time (Binance L2)

- **Feed**: WS de Binance (streams de `bookTicker` o depth incrementales).
- **Libro**: mantener L2 con `lastUpdateId`/`u` para consistencia.
- **Estrategia**: consume features L2 (no hay L3 real en público).

### Modo B — Replay determinista (ITCH-like)

- **Feed**: archivos binarios con eventos L3 simulados (ADD/MOD/DEL/TRADE) con timestamps.
- **Control de tiempo**: *time-warp clock* para reproducir ráfagas y medir latencia real.
- **Objetivo**: validar performance (tick-to-trade) y queue modeling.

---

## 4) Diseño de Componentes

### 4.1 Feed Handler

- Adapters: `BinanceAdapter`, `ReplayAdapter`.
- Normalización a eventos internos `MarketDataEvent` (struct, precios en ticks `long`).
- Publicación en **MDQ** (market-data queue) tipo ring buffer lock-free.
- Manejo de re-sync (gap detection) y backpressure.

### 4.2 Order Book Core

- **L2**: `BidSide`/`AskSide` con contenedores ordenados por precio.
- **L3 (sim)**: lista de órdenes por nivel con `OrderId`, qty, timestamp.
- Estructuras sugeridas (MVP): `SortedDictionary<long, Level>` + índice plano para top-N.
- Snapshots periódicos livianos para UI/diagnóstico.

### 4.3 Strategy Engine

- Interfaz `IStrategy` con `OnMarketData(...)` → `IEnumerable<OrderIntent>`.
- Estrategia MM: cotiza ±1–2 ticks del mid; re-quote ante cambios de OFI/spread; inventario target.
- Liquidity-Take: dispara `MARKET` o *agresivas* si OFI supera umbral y spread=1.

### 4.4 Risk / Controls

- `IRiskGate.Validate(OrderIntent)` con reglas: tamaño máx, notional máx, rate limit, inventory caps.
- Kill-switch global.

### 4.5 Order Gateway & Protocolos

- **Sim OUCH-like**: `NewOrder`, `Replace`, `Cancel`, `OrderAck`, `Fill`, `Reject`.
- **Paper (opcional)**: REST/WS a broker cripto con sandbox.
- *Round-trip* medido con timestamps monotónicos.

### 4.6 Matching Engine (Simulado)

- Libro propio por lado/price-level, prioridad **price-time**.
- Reloj interno para latencias configurables (e.g., 50–150 µs).
- Eventos `Ack/Fill` devueltos al Gateway.

### 4.7 Observabilidad y Métricas

- **HdrHistogram**: latencias (tick→decision, send→ack, end-to-end).
- Contadores: msgs/s, drops, re-syncs, rate limit hits, fill rate.
- Export Prometheus; dashboards Grafana (latencia p50/p95/p99, throughput, PnL).

### 4.8 UI (Trader Dashboard)

- React + Charting (TradingView o alternativa open-source) para:
  - **Depth** (DOM), **tape** (trades), **precio** en tiempo real.
  - Panel de **latencias** y **PnL** simulado.

---

## 5) Modelos de Datos (contratos)

> Nota: Los tipos se muestran en pseudo-.NET; precios y cantidades se representan como enteros escalados para evitar `decimal` en hot path.

```csharp
public readonly struct MarketDataEvent {
    public long Seq;          // secuencia del feed
    public long Ts;           // timestamp monotónico
    public Side Side;         // Bid/Ask
    public long PriceTicks;   // precio en ticks (p.ej., *10000)
    public long Qty;          // cantidad en unidades mínimas
    public EventKind Kind;    // Add/Update/Delete/Trade
}

public readonly struct OrderIntent {
    public OrderType Type;    // Limit/Market
    public Side Side;         // Buy/Sell
    public long PriceTicks;   // para Limit
    public long Qty;
    public long TsIn;         // timemark de origen (para latencia)
}

public readonly struct OrderAck {
    public long ClientOrderId;
    public AckKind Kind;      // Ack/Reject/CancelAck/ReplaceAck
    public long TsOut;
}

public readonly struct FillEvent {
    public long ClientOrderId;
    public long PriceTicks;
    public long Qty;
    public long TsFill;
}
```

---

## 6) Requisitos No Funcionales / Rendimiento

- **Latencia (MVP)**: tick→intent p99 < 800 µs (replay); intent→ack p99 < 300 µs (matcher sim).
- **Jitter**: σ de latencia < 100 µs en ráfagas.
- **Asignaciones**: \~0 en camino caliente; GC casi nulo durante picos.
- **Throughput**: ≥ 20–50k msgs/s en replay de 1 símbolo.

Optimización:

- `Server GC`, `Tiered PGO`, `ReadyToRun` opcional.
- `Span<T>`, `MemoryMarshal`, `ArrayPool<T>`; evitar boxing/allocs.
- Ring buffer lock-free (tipo Disruptor/Aeron-like) y *single-writer* en order book.

---

## 7) Seguridad y Confiabilidad

- **Kill-switch** manual y automático.
- Validaciones *fat finger* y límites de riesgo.
- Re-sync automático en gaps de secuencia (market data).
- Logs de auditoría asíncronos (fuera del hot path).

---

## 8) Desarrollo, Testing y Benchmarks

### Estructura de repositorio (monorepo)

```
/openhft-lab
  /feed           # adapters WS/Replay, normalización
  /book           # order book L2 real, L3 sim
  /strategy       # estrategias MM y Liquidity-Take
  /risk           # pre-trade checks
  /gateway        # OUCH-like sim, paper opcional
  /matcher        # matching engine in-memory
  /metrics        # HdrHistogram, exporters
  /bench          # escenarios de benchmark y asserts de latencia
  /ui             # dashboard React
  /docs           # diagramas, notas, guías
```

### Pipeline de CI

- Lints, tests unitarios y de integración.
- **Benchmarks** con límites (si p99 empeora >X%, falla el build).
- Publicación de imágenes Docker para cada servicio.

### Metodología de Benchmarks

- **Replay determinista** con perfiles de ráfagas.
- Medir:
  - Tick→Decision (feed→strategy)
  - Send→Ack (gateway↔matcher)
  - End-to-End (tick→fill)
- Exportar histogramas; reportes de regresión.

---

## 9) UI/UX — Dashboard (MVP)

- **Vista Mercado**: best bid/ask, spread, depth (top-10), trades recientes.
- **Vista Latencia**: gráficos p50/p95/p99 por tramo, histograma.
- **Vista Estrategia**: PnL simulado, fills, órdenes activas.

---

## 10) Roadmap (4–6 semanas)

**Semana 1**

- Monorepo + contratos base.
- Ring buffer lock-free en `metrics` o lib interna.
- `BinanceAdapter` + `ReplayAdapter` + snapshot/deltas.

**Semana 2**

- Order Book L2 (real) + L3 (simulado).
- Features: spread, depth, OFI.
- Estrategia MM v1 (cotizador básico) + RiskGate v1.

**Semana 3**

- Gateway OUCH-like + Matching Engine in-memory.
- Métricas de latencia con HdrHistogram y export Prometheus.
- Primer dashboard (precio, depth, latencias básicas).

**Semana 4**

- Hardening de latencia (pooled buffers, evitar GC, pinning básico).
- Tests de regresión de performance y alertas en CI.

**Semana 5** (Nice-to-have)

- Estrategia Liquidity-Take v1.
- Panel PnL simulado y fill rate.
- Artículo técnico (Dev.to/Medium) con resultados de latencia.

**Semana 6** (Nice-to-have)

- Modelado de posición en cola (queue position) en matcher.
- Replayer con perfiles de ráfaga realistas.

---

## 11) Guías de Implementación (resumen)

- Precios y cantidades en enteros escalados (ticks) para evitar `decimal`.
- Dispatch de order book **single-threaded** para consistencia y simplicidad.
- Evitar IO/serialización en el hot path; toda persistencia es asíncrona.
- Instrumentar cada frontera con timestamps para *budgets* de latencia.

---

## 12) Entregables del MVP

- Repositorio con:
  - **Código** de todos los módulos + scripts Docker.
  - **Dashboards** Grafana listos.
  - **Docs**: README principal, guía de arranque, diagramas en `/docs`.
- **Demo** en video corto (Loom/YouTube) mostrando flujo y métricas.

---

## 13) Próximos pasos sugeridos

1. Crear repo y *scaffold* de proyectos (plantillas .NET + React + Docker Compose).
2. Implementar `ReplayAdapter` y Order Book L2 con tests.
3. Añadir estrategia MM + matcher sim + métricas p50/p95/p99.
4. Publicar primeras gráficas y escribir el post técnico inicial.

---

### Apéndice A — Glosario breve

- **L1/L2/L3**: niveles de profundidad del libro (mejor precio, profundidad por niveles, órdenes por-id).
- **OFI**: *Order Flow Imbalance*; indicador de presión de compra/venta.
- **Tick-to-Trade**: latencia desde un cambio de mercado hasta decidir y emitir una orden.
- **Jitter**: variación de la latencia; deseable que sea baja.
- **Price-Time Priority**: reglas del matcher para asignar fills.

### Apéndice B — Referencias técnicas internas (a implementar en `/docs`)

- Diagramas UML de clases (Order Book, Gateway, Matcher).
- Secuencias para: Snapshot→Deltas→Re-sync; New→Ack→Fill.
- Presets de runtime (.NET, kernel) para reproducibilidad de benchmarks.

