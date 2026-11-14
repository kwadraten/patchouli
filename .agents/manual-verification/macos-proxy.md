# Manual verification: proxy support on macOS (issue #56)

> **Status: not yet executed on real hardware.** This checklist must be run on a
> physical macOS machine before issue #56 can be called verified. The host-side
> implementation landed without macOS access; every step below is unverified on
> that platform until checked.

## Scope

Verifies the `Network` settings section (`NetworkAppSettings`, proxy modes
`system` / `manual` / `none`), the `IHttpTransportPolicy` applied to all
host-owned HTTP clients, the `NetworkConnectionTestService` probe, and proxy
credential storage in the app credential store (`ICredentialStore`, provider id
`network-proxy`).

Out of scope on purpose: the loopback channel between the app and its own
runtime host/CLI never honors any proxy setting — that path must keep working
in every scenario below. Also, `ServerCertificateCustomValidationCallback` is
never weakened; TLS-intercepting proxies are handled only by trusting their CA
in the OS.

## Test matrix

| # | Scenario | Expected |
|---|----------|----------|
| 1 | macOS system proxy configured (System Preferences → Network → Proxies), app mode `system` | All host HTTP traffic uses the system proxy; PAC is resolved by .NET's system proxy resolution |
| 2 | Mode `manual`, `http://proxy:8080` | Traffic tunnels through the manual proxy |
| 3 | Mode `manual`, authenticated proxy (HTTP + Basic) | Proxy password is read from the credential store (`network-proxy`); a wrong/absent password surfaces as `authentication` (407), not a generic failure |
| 4 | Mode `manual`, `socks5://` and `socks5h://` | Both work; `socks5h` maps onto .NET socks5 (remote DNS already) |
| 5 | Mode `none` | Direct connection regardless of any system proxy |
| 6 | Manual mode + bypass list (wildcards `*`/`?`, optional `:port`) | Matching hosts connect directly; loopback (`localhost`, `127.0.0.1`, `::1`) is always bypassed |
| 7 | TLS-intercepting proxy (enterprise MITM) | Without the CA trusted: probe reports `tls` with a message instructing to trust the CA in the OS — **never** a silent bypass. After adding the CA to the login Keychain with trust settings, traffic succeeds |
| 8 | Mode `manual` with no address | Runtime connects directly (autosaved draft state); settings validation reports `proxy_address_required` |
| 9 | Mode `manual` with a malformed or unsupported scheme address | Connection test reports `invalid_configuration`; `GetProxy` fails fast instead of silently going direct |

## Procedure

### Preparation

1. Build/run the app on macOS (`dotnet run` on the UI project or a published bundle).
2. For scenarios 3/7, stand up a local proxy:
   - Authenticated HTTP: `mitmproxy --mode regular` with auth, or a Squid container with `proxy_auth`.
   - TLS interception: `mitmproxy` default mode (its CA is used in step 7).
   - SOCKS5: `ssh -D 1080 localhost` or a `dante` container.

### Steps

1. **System mode** — configure an HTTP proxy in macOS System Settings → Network →
   (interface) → Proxies. In Patchouli open 设置 → 行为 → 链接与系统 → 网络代理 and
   choose 系统代理. Click 测试连接: it first saves and applies the section, then
   calls `HostServices.NetworkConnectionTest.TestAsync()` off the UI thread
   (probe target `https://api.github.com/`). Expect 「连接成功」. Trigger a CSL
   style refresh and an extension catalog fetch; both should succeed and appear
   in the proxy's log.

   UI reference for the later steps: 手动 reveals 地址, 需要排除的网址 (one host
   name or domain per line), 用户名 and 密码 (masked; leaving it blank keeps the
   saved password, the placeholder shows 「已保存（留空则不变）」 when one exists).
   清除凭据 removes the stored password. 不使用代理 maps to mode `none`. Invalid
   addresses show a short Chinese error under 地址 and are never saved. Results
   map each `Kind` to short Chinese text (e.g. `tls` → a hint to trust the proxy's
   CA in the system keychain); no address, username, password or raw exception is
   shown.
2. **Manual HTTP** — set mode `manual`, address `http://127.0.0.1:<port>` with a
   local proxy running. Repeat the probe and feature checks.
3. **Authenticated proxy** — enter username in settings and save the password
   through the UI (it must land in the app credential store — the `Credentials`
   section of the settings file — under provider id `network-proxy`, never in
   the `Network` section). Confirm: the password does not appear in the
   `Network` section, in logs, or in the
   `DescribeForDiagnostics()` output. With a wrong password the probe must
   classify `authentication` (HTTP 407); after fixing it, `ok`.
4. **SOCKS5** — `socks5://127.0.0.1:1080` and `socks5h://127.0.0.1:1080`; both
   should pass the probe. Confirm remote DNS works for `socks5h` by probing a
   name resolvable only upstream.
5. **None** — with a system proxy still configured, set mode `none`. The probe
   must connect directly (verify the proxy log shows nothing).
6. **Bypass list** — manual proxy + bypass entries like `*.internal.example`,
   `10.0.0.?`, `example.com:8443`. Verify bypassed hosts hit the origin directly
   and others go through the proxy. `localhost` must bypass even with no entry —
   the runtime host channel (`RuntimeHostCoordinator` / CLI) must stay functional
   under every proxy mode.
7. **TLS interception** — with `mitmproxy` intercepting and its CA **not**
   trusted: the probe must report `tls` (handshake failure), and no code path may
   silently accept the certificate. Then import `mitmproxy-ca-cert.pem` into the
   login Keychain, set "Always Trust", and re-run: expect `ok`.
8. **Draft/invalid manual config** — mode `manual` with an empty address:
   validation shows `proxy_address_required`, runtime traffic goes direct. With
   `gopher://x` or `http://user:pass@host`: probe reports `invalid_configuration`.
9. **Live setting change** — change mode/address while the app is running
   (`HostServices.UpdateNetworkSettings`); the next request must honor it
   without restarting (the injected `IWebProxy` resolves per request).
10. **Feature sweep** — under the working manual proxy, exercise each client:
    CSL style download, MinerU requests, agent runtime installer download
    (`HostHttpPurpose.AgentDownload`), LLM provider calls and the subscription
    client (`HostHttpPurpose.Subscription`), extension file downloads. All must
    traverse the proxy.

### What to check for leaks

- The settings file's `Network` section contains mode, address, username,
  `hasProxyPassword`, bypass — **never** a password (passwords live only in the
  `Credentials` section via `ICredentialStore`).
- Logs and diagnostics never print the proxy URL, username or password
  (`DescribeForDiagnostics` emits only `manual (address set)` / `system` / `none`).
- `NetworkConnectionTestResult.Summary` strings contain no target URL or proxy
  hostname.

## Failure classification reference

| `Kind` | Meaning |
|--------|---------|
| `ok` | Probe succeeded |
| `proxy_tunnel` | CONNECT tunnel refused by the proxy |
| `tls` | TLS handshake/validation failed (e.g. untrusted MITM CA) |
| `dns` | Name resolution failed |
| `connection` | TCP connect failed |
| `authentication` | 407 proxy auth, or 401/403 origin auth |
| `timeout` | Request timed out |
| `invalid_configuration` | Manual address malformed / probe URL invalid |
| `canceled` / `error` | Cancellation, or anything unclassified |
