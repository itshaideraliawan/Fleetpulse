# FleetPulse portal

The portal uses Fleet API for REST and the operator SignalR hub. Start Aspire
with `dotnet run --project src/AppHost`, then copy the Fleet API HTTP URL from
the Aspire dashboard. In a second PowerShell window:

```powershell
cd src/portal
$env:FLEET_API_URL = 'http://localhost:<Fleet API port>'
npm install
npm run dev
```

Open the Vite URL. The dev server proxies `/api` and `/hubs` to Fleet API so
the browser uses one origin. If Aspire assigns a new Fleet API port, update
`FLEET_API_URL` and restart Vite.

The REST SDK in `src/api/generated` comes from `openapi.json`, which is a
snapshot of Fleet API's `/openapi/v1.json`. After API contract changes, replace
that snapshot with the updated document, remove its local `servers` entry,
and run `npm run generate:api`. Review the generated diff together with the
server change. The SignalR payload is separate from OpenAPI and typed in the
portal's live status module.
