[CmdletBinding()]
param([Parameter(Mandatory)][string]$ModelDirectory)

$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($ModelDirectory)
[IO.Directory]::CreateDirectory($directory) | Out-Null
$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromHours(2)

function Save-VerifiedFile {
    param(
        [string]$Name,
        [string]$Url,
        [long]$ExpectedBytes,
        [string]$ExpectedSha256,
        [long]$RangeStart = -1,
        [long]$RangeEnd = -1
    )

    $destination = Join-Path $directory $Name
    if (Test-Path -LiteralPath $destination) {
        $file = Get-Item -LiteralPath $destination
        $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if ($file.Length -eq $ExpectedBytes -and $hash -eq $ExpectedSha256) {
            Write-Output "Verified $Name"
            return
        }
        throw "Existing model file '$destination' did not match the pinned release."
    }

    $temporary = "$destination.partial-$([Guid]::NewGuid().ToString('N'))"
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $Url)
    if ($RangeStart -ge 0) {
        $request.Headers.Range = [Net.Http.Headers.RangeHeaderValue]::new($RangeStart, $RangeEnd)
    }

    try {
        $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            $response.EnsureSuccessStatusCode() | Out-Null
            if ($RangeStart -ge 0 -and $response.StatusCode -ne [Net.HttpStatusCode]::PartialContent) {
                throw "The model server did not honor the byte range for '$Name'."
            }

            $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $target = [IO.File]::Create($temporary)
            try {
                $source.CopyTo($target)
            }
            finally {
                $target.Dispose()
                $source.Dispose()
            }
        }
        finally {
            $response.Dispose()
        }

        $file = Get-Item -LiteralPath $temporary
        $hash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
        if ($file.Length -ne $ExpectedBytes -or $hash -ne $ExpectedSha256) {
            throw "The downloaded model file '$Name' did not match the pinned release."
        }

        Move-Item -LiteralPath $temporary -Destination $destination
        Write-Output "Downloaded $Name"
    }
    finally {
        $request.Dispose()
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary
        }
    }
}

try {
    Save-VerifiedFile -Name 'ovisocr3_int4_single.onnx' `
        -Url 'https://huggingface.co/arlenecc/OvisOCR3-ONNX-int4/resolve/e654df1eed2c3b4a902eac7b458bddd7cb338e1b/onnx/ovisocr3_int4_single.onnx' `
        -ExpectedBytes 1216759027 -ExpectedSha256 '7bafbef06e85847963d46fe520ba9767d18c7d2abdad91a25ce2f036558bcb3e'
    Save-VerifiedFile -Name 'tokenizer.json' `
        -Url 'https://huggingface.co/ATH-MaaS/OvisOCR3/resolve/4833d5d2afa6e769ee933f024504d67ace80a24c/tokenizer.json' `
        -ExpectedBytes 12807982 -ExpectedSha256 '5f9e4d4901a92b997e463c1f46055088b6cca5ca61a6522d1b9f64c4bb81cb42'
    Save-VerifiedFile -Name 'document_structured.txt' `
        -Url 'https://huggingface.co/ATH-MaaS/OvisOCR3/resolve/4833d5d2afa6e769ee933f024504d67ace80a24c/prompts/document_structured.txt' `
        -ExpectedBytes 1110 -ExpectedSha256 '1b6139ec72888e087c8af75f2e2dfb78b425e15ae1c1529940b96137613d1db1'
    Save-VerifiedFile -Name 'visual_pos_embed.bf16' `
        -Url 'https://huggingface.co/ATH-MaaS/OvisOCR3/resolve/4833d5d2afa6e769ee933f024504d67ace80a24c/model.safetensors' `
        -ExpectedBytes 3538944 -ExpectedSha256 '5f6b1b11dc866dc90f773a3e95c7b0ae077cd1744161bccbdc1302050986c287' `
        -RangeStart 1702491584 -RangeEnd 1706030527
}
finally {
    $client.Dispose()
}
