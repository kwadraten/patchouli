#load "../lib/Common.fsx"
open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Common

let endpoint = "https://api.runpod.io/v2/pods"
let request key methodName (url: string) (body: JsonNode) (seconds: float) =
    use client = new HttpClient(Timeout = TimeSpan.FromSeconds(seconds))
    use query = new HttpRequestMessage(HttpMethod(methodName), url)
    query.Headers.Authorization <- AuthenticationHeaderValue("Bearer", key)
    if not (isNull body) then query.Content <- new StringContent(body.ToJsonString(), utf8, "application/json")
    use response = client.SendAsync(query, cancellation.Token).GetAwaiter().GetResult()
    if not response.IsSuccessStatusCode then failwith $"RunPod request failed (HTTP {int response.StatusCode})"
    JsonNode.Parse(response.Content.ReadAsStringAsync(cancellation.Token).GetAwaiter().GetResult())

entry (fun argv ->
    let args = parse ["--check-account"] ["--action"; "--pod-id"; "--public-key-file"] argv
    if has "help" args then printfn "Usage: dotnet fsi scripts/oxr-conversion/plan-runpod.fsx -- [--action Plan|Create|Get|Stop] [--check-account] [--pod-id ID] [--public-key-file FILE]"
    else
        let action = choice "--action" "Plan" ["Plan"; "Create"; "Get"; "Stop"] args
        let podId = arg "--pod-id" "" args
        if List.contains action ["Get"; "Stop"] && not (Regex.IsMatch(podId, "^[a-z0-9]{8,32}$")) then failwith "Specify a valid RunPod Pod ID"
        let pod = jobj ["name", jvalue "oxr-onnx-export";
                        "image", jvalue "runpod/pytorch:1.4.0-cu1300-torch2120-ubuntu2404@sha256:93396b43c615b051e3951adbed2f958acff7d645936caa4029a58d0c1d309abd";
                        "cloud", jvalue "SECURE"; "gpu", jobj ["id", jvalue "NVIDIA A100-SXM4-80GB"; "count", jvalue 1; "minCudaVersion", jvalue "13.0"];
                        "disk", jvalue 80; "mounts", jobj ["persistent", jobj ["size", jvalue 100; "path", jvalue "/workspace"]];
                        "ports", jvalue ["22/tcp"]; "startSsh", jvalue true; "env", jobj ["HF_HOME", jvalue "/workspace/hf-cache"; "HF_HUB_DISABLE_TELEMETRY", jvalue "1"]]
        if action = "Create" then
            let path = arg "--public-key-file" "" args
            if not (File.Exists(path)) then failwith "A local SSH public key is required to create the conversion Pod"
            let publicKey = File.ReadAllText(path).Trim()
            if not (Regex.IsMatch(publicKey, @"^ssh-ed25519 [A-Za-z0-9+/=]+(?:\s+.*)?$")) then failwith "An Ed25519 SSH public key is required"
            pod.["env"].["PUBLIC_KEY"] <- jvalue publicKey
            pod.["env"].["SSH_PUBLIC_KEY"] <- jvalue publicKey
        let key =
            if action = "Plan" && not (has "--check-account" args) then ""
            else
                let dotenv = resolve ".env"
                if not (File.Exists(dotenv)) then failwith "RUNPOD_API_KEY is not available: repository .env is missing"
                let entries = Regex.Matches(File.ReadAllText(dotenv), @"(?m)^\s*RUNPOD_API_KEY\s*=(.*)$")
                if entries.Count = 0 then failwith "RUNPOD_API_KEY is not configured in .env"
                let key = entries[entries.Count - 1].Groups[1].Value.Trim().Trim('"', '\'')
                if key = "" then failwith "RUNPOD_API_KEY is empty in .env"
                key
        if has "--check-account" args then request key "GET" endpoint null 30. |> ignore; printfn "RunPod read-only API authentication succeeded"
        let state =
            match action with
            | "Get" -> request key "GET" (endpoint + "/" + podId) null 30.
            | "Stop" -> request key "POST" (endpoint + "/" + podId + "/action") (jobj ["action", jvalue "stop"]) 60.
            | "Create" ->
                let existing = request key "GET" endpoint null 30.
                if items existing.["pods"] |> List.exists (fun p -> get "name" p = get "name" pod) then failwith "A conversion Pod with this name already exists; inspect it before creating another"
                request key "POST" endpoint pod 90.
            | _ -> printfn "RunPod Pod request plan (dry-run only, no resource was created):\nPOST %s" endpoint; pod
        let output =
            if action = "Plan" then state
            else
                let result = jobj [for key in ["id"; "status"; "cost"; "cudaVersion"] do let value = prop key state in key, (if isNull value then null else value.DeepClone())]
                if action <> "Create" then
                    result.["SshDirect"] <- (prop "ssh" state |> prop "direct" |> fun node -> if isNull node then null else node.DeepClone())
                    result.["TcpPorts"] <- jarray (prop "runtime" state |> prop "ports" |> items |> List.filter (fun p -> get "type" p = "tcp") |> List.map (fun p -> jobj [for key in ["ip"; "public"; "private"] do let value = prop key p in key, (if isNull value then null else value.DeepClone())] :> JsonNode))
                result
        printfn "%s" (output.ToJsonString(jsonOptions))
    0)
