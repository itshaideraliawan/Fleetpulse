# FleetPulse portal

The portal uses Fleet API for REST and the operator SignalR hub. Start Aspire
with `dotnet run --project src/AppHost`. Fleet API is exposed at
`http://localhost:5000`, matching the portal proxy default. In a second PowerShell window:

```powershell
cd src/portal
npm.cmd ci
npm.cmd run dev
```

Open the Vite URL. The dev server proxies `/api` and `/hubs` to Fleet API so
the browser uses one origin. Set `FLEET_API_URL` before starting Vite only when
using a separately hosted Fleet API at another address.

On Windows, `npm.cmd` avoids PowerShell's script-policy block on `npm.ps1`.
On other systems, use `npm` instead.

## Persistent SQL credentials

AppHost has a stable user-secrets identity so generated development passwords
survive restarts alongside the SQL data volume. Password values are not committed.
A volume created before this identity was configured may require its original
`sa` password. Save that existing password locally without placing it in a command
argument or printing it:

```powershell
$sqlPassword = Read-Host 'Existing SQL sa password' -AsSecureString
@{ 'Parameters:sqlserver-password' = [Net.NetworkCredential]::new('', $sqlPassword).Password } |
    ConvertTo-Json -Compress | dotnet user-secrets set --project src/AppHost
Remove-Variable sqlPassword
```

Run these commands from the repository root. This reuses the existing login;
it does not change SQL credentials or delete data. If the original password is
unknown, use a separate named rehearsal volume and leave the original intact:

```powershell
dotnet run --project src/AppHost -- --Simulator:Devices=5000 --SqlServer:DataVolumeName=fleetpulse-rehearsal
```

Keep the same volume name and local secret on later starts to reuse that rehearsal
database. Port 5000 must be available for the Fleet API endpoint.
When switching between volumes initialized with different passwords, set the
local SQL password parameter to the credential for the selected volume.

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

## Demo Control

Start Aspire and the portal as above, then open Demo control. The screen polls
once per second and shows actual device sessions across active Gateways. Apply
settings and wait for **Applied** before starting a scenario. Requests carry an
expected revision and request ID; uncertain writes are not automatically retried.

- **Inject bad release:** enable it, select version `2.4.0` and model `Kiosk-B`,
  then Apply settings. Launch the same version on Rollouts. A cohort of the first
  100 devices passes wave 1 and fails wave 2. A 5000-device canary includes all
  models and can fail wave 1; this control does not defer injection until wave 2.
  Apply settings first to override any simulator startup fault flags.
- **Admission control:** check or clear the toggle and Apply settings. All
  reporting Gateways use the saved setting; rate/burst remain per-instance.
- **Drop connections:** closes device sockets on every Gateway. The chart uses
  actual session counts, not the telemetry online count, which has a 45-second
  offline window. Compare two drops with admission enabled/disabled, keeping
  other conditions fixed. A setting change during a run is labeled explicitly.
- **Reset fleet:** confirm history deletion, then Reset. Active commands or
  nonterminal rollouts return a conflict. Reset clears command/release/rollout
  history and hot telemetry, restores SQL/device firmware 1.0.0, disables fault
  injection, enables admission, and reconnects devices. New work is blocked
  until current Gateway sessions acknowledge reset. Stale telemetry and commands
  cannot undo the baseline. Inventory identities and site/region data remain.

Recovery curves retain the latest two runs in this mounted screen, sample for
up to 180 seconds, and clear on reset or navigation. A very fast drop/reconnect
can happen between samples. Gateway reports expire after five seconds; a stopped
Gateway ages out. These are demo observations, not the D6-03 benchmark results.
If a reset remains pending, restore Gateway/Redis availability and wait for
device configuration acknowledgement; do not start another rollout.

Controls are for the local demo and have no authentication, matching the project
scope. Keep the local stack on trusted interfaces. Fleet API publishes through
its SQL transactional outbox; Redis is the versioned projection consumed by
Gateways, and only Gateways send device control messages.
