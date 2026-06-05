using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FileNinja.FlowLauncher.Ipc;

internal sealed class IpcRequest
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("method")] public string? Method { get; set; }
    [JsonPropertyName("params")] public IpcParams? Params { get; set; }
}

internal sealed class IpcParams
{
    [JsonPropertyName("max")] public int? Max { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("pane")] public string? Pane { get; set; }
    [JsonPropertyName("newTab")] public bool NewTab { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
}

internal sealed class IpcResponse
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("result")] public IpcResult? Result { get; set; }
}

internal sealed class IpcResult
{
    [JsonPropertyName("version")] public int? Version { get; set; }
    [JsonPropertyName("pid")] public int? Pid { get; set; }
    [JsonPropertyName("ready")] public bool? Ready { get; set; }
    [JsonPropertyName("items")] public List<IpcItem>? Items { get; set; }
    [JsonPropertyName("query")] public string? Query { get; set; }
}

internal sealed class IpcItem
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("fullPath")] public string? FullPath { get; set; }
    [JsonPropertyName("isDirectory")] public bool IsDirectory { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}
