# 🚀 Quick Start: G4 Bot Monitor

[![Build, Test & Release G4™ Bot Monitor](https://github.com/g4-api/g4-bot-monitor/actions/workflows/release-pipline.yml/badge.svg)](https://github.com/g4-api/g4-bot-monitor/actions/workflows/release-pipline.yml)

`g4-bot-monitor` is a lightweight, single-file SignalR client designed to **shadow a bot** and **sync its status** to a central SignalR hub. It also exposes a tiny HTTP listener so the bot can report its own status via local HTTP requests.

---

## ⚙️ CLI Usage

```bash
g4-bot-monitor --HubUri=<hub-url> --Name=<bot-name> --Type=<bot-type> --ListenerUri=<listener-url> --ListenerPort=<port>
```

All parameters are **required**:

| Parameter        | Description                                                                 |
|------------------|-----------------------------------------------------------------------------|
| `--HubUri`       | SignalR hub URL (e.g., `http://localhost:9944/hub/v4/g4/bots`)              |
| `--Name`         | Human-readable bot name (e.g., `"InvoiceBot"`)                              |
| `--Type`         | Bot type/category (e.g., `"Static Bot"`, `"File Listener"`)                 |
| `--ListenerUri`  | Base URI where the monitor listens (e.g., `http://localhost:8080`)          |

---

## 📡 HTTP Listener Endpoints

After startup, the monitor begins listening at:

```
<ListenerUri>/monitor/
```

### ✅ 1. `GET /monitor/ping`

Simple health check — always returns `200 OK`.

```bash
curl http://localhost:8080/monitor/ping
```

**Response:**

```json
{ "message": "pong" }
```

---

### 📤 2. `POST /monitor/update`

Used by the bot to send **status updates** to the SignalR hub.

**Expected payload:**

```json
{ "status": "Working" }
```

**Example request:**

```bash
curl -X POST http://localhost:8080/monitor/update \
     -H "Content-Type: application/json" \
     -d '{ "status": "Working" }'
```

**Success Response:**

```json
{ "message": "Connected bot successfully updated." }
```

**Failure Response:**

```json
{
  "error": "ExceptionType",
  "message": "Stack trace or reason"
}
```

---

## 🧪 Full Example

```bash
./g4-bot-monitor-linux-x64 \
  --HubUri=http://localhost:9944/hub/v4/g4/bots \
  --Name=Bot42 \
  --Type="Static Bot" \
  --ListenerUri=http://localhost:8080
```

---

## ✅ Runtime Behavior

- Retries connection and registration up to 10 minutes
- Exposes `/monitor/ping` and `/monitor/update` endpoints
- Listens for `ReceiveHeartbeat` and `ReceiveRegisterBot` from the hub
- Handles Ctrl+C, shutdown signals, and graceful cleanup
- Skips failed update attempts (non-blocking)
