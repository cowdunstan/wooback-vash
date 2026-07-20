# ca-certs — local TLS-inspection workaround

This directory lets the Docker **build** trust extra root CAs. It exists for one
reason: some dev machines run a TLS-inspecting proxy or antivirus (this repo hit
**AVG Web/Mail Shield**) that MITMs HTTPS and re-signs it with a private root CA.
The host trusts that root, but the Linux base images inside Docker do not, so
`dotnet restore` (and any container HTTPS) fails with:

```
error NU1301: Unable to load the service index for source https://api.nuget.org/...
# underlying: unable to get local issuer certificate
```

## Fix (local only)

Export the intercepting root CA and drop it here as a `.crt` (PEM). On Windows,
for the AVG root:

```powershell
$c = Get-ChildItem -Recurse Cert:\LocalMachine\Root |
     Where-Object { $_.Subject -like "*AVG Web/Mail Shield*" } | Select-Object -First 1
$pem = "-----BEGIN CERTIFICATE-----`n" +
       [Convert]::ToBase64String($c.RawData,'InsertLineBreaks') +
       "`n-----END CERTIFICATE-----`n"
Set-Content -Path .\avg-root.crt -Value $pem -Encoding ascii
```

The Dockerfile copies `*.crt` from here into the image and runs
`update-ca-certificates` before restore.

## Not committed

The actual `*.crt` files are git-ignored — they're machine-specific and not part
of the app. **Fly.io and CI builders have no such proxy**, so there the directory
holds only `.gitkeep` and the trust step is a harmless no-op. If a teammate hits
the same error, they run the snippet above on their own machine.
