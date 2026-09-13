# Smart-X

An IoT telemetry ingestion and mesh-management system, built as a university Portfolio of Evidence.

Smart-X is designed to sit between a field network of sensors and the people who need to act on
what those sensors report. The brief covers three pillars:

1. **Sensor Data Ingestion and Telemetry** — accept readings from field devices, validate them, and
   store the resulting telemetry stream.
2. **Real-Time Command Stream and History** — push commands out to devices and keep a replayable
   record of what was sent and when. *(not yet implemented)*
3. **Network Topology and Mesh Routing** — map the mesh, track which nodes can still reach the
   gateway, and route around failures. *(not yet implemented)*

The first pillar is built: sensor registration into a validated zone hierarchy, a staged telemetry
pipeline, a recency-weighted trust engine behind a live dashboard, and encrypted file attachments per
sensor.

## Layout

```
SmartX.sln
src/
  SmartX.Shared/    contracts shared by the API and the client
  SmartX.Api/       ASP.NET Core Minimal API
  SmartX.Client/    Blazor WebAssembly app (MudBlazor)
tests/
  SmartX.Tests/     xUnit
docs/
```

`SmartX.Api` and `SmartX.Client` both reference `SmartX.Shared`. The client does **not** reference
the API — it calls it over HTTP, which is what keeps the two genuinely separate.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

Check what you have:

```bash
dotnet --version
```

## Restore and build

From the repository root:

```bash
dotnet restore
```

```bash
dotnet build
```

## Running

The API and the client are separate applications. Both need to be running, in two terminals.

**Terminal 1 — the API:**

```bash
dotnet run --project src/SmartX.Api
```

**Terminal 2 — the client:**

```bash
dotnet run --project src/SmartX.Client
```

Then open <http://localhost:5131>. The landing page reports whether it reached the API. If it says
the API is unreachable, the API is not running or is on a different port.

## Ports

| Application | HTTP | HTTPS |
| --- | --- | --- |
| `SmartX.Api` | `5266` | `7059` |
| `SmartX.Client` | `5131` | `7258` |

Two settings have to agree for the connection to work, and both are configuration rather than
compiled-in constants:

- `src/SmartX.Client/wwwroot/appsettings.json` → `ApiBaseAddress` points the client at the API.
- `src/SmartX.Api/appsettings.Development.json` → `Cors:AllowedOrigins` lists the client origins
  the API will accept cross-origin requests from.

Change a port and you must change both.

## Tests

```bash
dotnet test
```

## Attachment encryption key

Attachments are encrypted at rest with AES-256-GCM. The key is **never** in source code or in any
committed settings file.

**In development**, set one in the API project's user secrets, which live in your user profile outside
the repository:

```bash
dotnet user-secrets set "Attachments:EncryptionKey" "<base64-encoded 32-byte key>" --project src/SmartX.Api
```

Generate a key with PowerShell:

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

If you skip this, the API still runs in the Development environment. It generates a temporary key for
the life of the process and logs a warning. Attachments then become unreadable after a restart. So does
their index, which is held in memory, so nothing that was still reachable is lost.

**Outside development**, supply the key as the environment variable `Attachments__EncryptionKey`. The
API refuses to start without a valid one.

### What this does and does not demonstrate

This is a demonstration of the *encryption mechanism*, not a production key-management story.

- **Demonstrated:** streamed authenticated encryption with a per-file random nonce, tamper detection on
  every chunk, no plaintext written to disk, and a key supplied through configuration rather than code.
- **Not demonstrated:**
  - A real deployment would fetch the key from a managed key vault (Azure Key Vault, AWS KMS, HashiCorp
    Vault), keep it out of the process environment, and audit every access.
  - Keys would be rotated, which needs a key identifier stored in each file's header so old files can still
    be decrypted. This format has a version byte for that change but no key id yet.
  - Envelope encryption — a per-file data key, itself encrypted by a master key — is the usual approach, and
    is not implemented here.

## API

| Method | Route | Returns |
| --- | --- | --- |
| `GET` | `/api/health` | `{ "status": "ok" }` |
| `GET` `POST` | `/api/sensors` | List, or register (`201`, `400`, `409`) |
| `GET` | `/api/sensors/{mac}` | One sensor, or `404` |
| `POST` | `/api/telemetry` | One reading (`202`, `400`, `404`) |
| `POST` | `/api/telemetry/batch` | Jagged batch of readings (`202`) |
| `GET` | `/api/sensors/{mac}/telemetry` | Retained readings, oldest first |
| `GET` | `/api/zones` | The zone hierarchy, nested |
| `GET` | `/api/trust` | Every node worst first, each with recent history |
| `GET` | `/api/trust/summary` | Mesh confidence and per-band counts |
| `GET` | `/api/trust/{mac}` and `/history` | One node, and its full trust series |
| `POST` | `/api/sensors/{mac}/attachments` | Streamed multipart upload, one file (`201`, `400`, `404`, `413`, `415`) |
| `GET` | `/api/sensors/{mac}/attachments` | A sensor's attachments, newest first |
| `GET` | `/api/attachments/limits` | Maximum size and permitted extensions |
| `GET` | `/api/attachments/{id}` | Download, decrypted on the way out |
| `GET` | `/api/attachments/{id}/thumbnail` | An image attachment for inline display |
| `DELETE` | `/api/attachments/{id}` | `204`, or `404` |

## Attachments: design notes and limits

- **Streamed, not buffered.** Uploads are read with `MultipartReader` straight off the request body.
  `IFormFile` binding would buffer the whole file into memory or a temp file before the handler ran.
  A 1 GiB log round-trips while the API's private memory rises by about 6 MB.
- **An oversized upload closes the connection.** Once the limit is passed the server stops reading and
  answers `413`. A client still sending may see the connection close instead of reading the `413`,
  because draining an arbitrarily large body just to reply politely would defeat the limit. The web
  client checks size before uploading, so this only affects clients that skip that check.
- **Thumbnails are the original image.** A resized thumbnail needs an image-decoding library and a full
  decode of the picture into memory, which is exactly the cost the streaming design avoids. The client
  scales the image down for display.
- **Text is UTF-8 only.** A UTF-16 log contains NUL bytes and is rejected as binary.
- **Storage is in-memory metadata over files on disk** (`App_Data/attachments`, git-ignored). After a
  restart the encrypted files remain but are no longer indexed.
