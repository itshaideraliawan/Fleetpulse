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

## Rollouts

The Rollouts screen starts a release and follows its persisted waves, health
gates, transition reasons, and automatic rollback. Region and model filters
intersect; leaving both unrestricted selects across the fleet. The device limit
selects the first matching serial numbers, including offline devices. The server
rejects overlapping active cohorts. Launch requests are not automatically retried;
after an uncertain response, check recent rollouts before starting another.

For the wave-2 failure scenario, start Aspire with 100 simulated devices:

```powershell
dotnet run --project src/AppHost -- --Simulator:Devices=100 --Simulator:BadReleaseVersion=2.4.0 --Simulator:BadReleaseModel=Kiosk-B
```

Start the portal as above, wait for the devices to connect, then select Rollouts.
Use firmware `2.4.0`, all regions, all models, and maximum devices `100`.
Select **Start rollout**. The 1-device canary passes; the next 9-device wave
fails on Kiosk-B. The recorded halt reason stays visible after automatic rollback
finishes. Seven successfully updated devices should be restored to their original
firmware. Waves 3 and 4 stay unevaluated. The transition list preserves the brief
GateCheck and Halted states even when polling only observes RollingBack.

Choose `2.4.1` to see all four waves pass. Keep the simulator running: unknown
install results or failed/expired rollback commands keep rollback open. For the
documented counts, use a fresh fleet database and simulator state; each plan
captures the devices' current SQL firmware as its rollback destination.

The selected rollout is polled once per second with overlapping requests
prevented; recent history is refreshed every five seconds. Errors retain the last
results with a stale-data notice. No additional operator-hub pushes are added.
