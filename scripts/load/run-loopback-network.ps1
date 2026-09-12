param(
    [Parameter(Mandatory = $true)][ValidatePattern('^http://(127\.0\.0\.1|localhost)(:\d+)?/?$')][string]$BaseUrl,
    [int]$Rps = 50,
    [int]$Duration = 30,
    [int]$Connections = 8,
    [string]$Results = 'artifacts/load-loopback.json'
)

# Start Signalboard separately with a disposable local database and its synthetic demo identity.
# This command intentionally rejects remote hosts; it is a socket/loopback profile, not a live test.
dotnet run --project scripts/load/Lakewright.LoadHarness/Lakewright.LoadHarness.csproj -c Release --no-build -- `
  --base-url=$BaseUrl --rps=$Rps --duration=$Duration --connections=$Connections `
  --error-rate=0.001 --results=$Results
