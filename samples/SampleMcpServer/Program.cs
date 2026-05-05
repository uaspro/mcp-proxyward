using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "Sample MCP Server"
}));

app.MapPost("/mcp", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    var payload = await JsonNode.ParseAsync(request.Body, cancellationToken: cancellationToken);
    JsonNode response = payload switch
    {
        JsonArray batch => new JsonArray(batch.Select(message => (JsonNode?)CreateResponse(message)).ToArray()),
        JsonObject message => CreateResponse(message),
        _ => CreateError(null, "Invalid JSON-RPC payload")
    };

    return Results.Json(response);
});

app.Run();

static JsonObject CreateResponse(JsonNode? message)
{
    if (message is not JsonObject obj)
    {
        return CreateError(null, "Invalid JSON-RPC message");
    }

    var id = obj["id"]?.DeepClone();
    var method = obj["method"]?.GetValue<string>();

    return method switch
    {
        "tools/list" => CreateResult(id, new JsonObject
        {
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "echo",
                    ["title"] = "Echo",
                    ["description"] = "Returns the supplied message.",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["message"] = new JsonObject
                            {
                                ["type"] = "string"
                            }
                        }
                    }
                }
            }
        }),
        "tools/call" => CreateToolCallResult(id, obj["params"] as JsonObject),
        _ => CreateResult(id, new JsonObject())
    };
}

static JsonObject CreateToolCallResult(JsonNode? id, JsonObject? parameters)
{
    var arguments = parameters?["arguments"] as JsonObject;
    var message = arguments?["message"]?.GetValue<string>() ?? "Hello from sample MCP.";

    return CreateResult(id, new JsonObject
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = message
            }
        }
    });
}

static JsonObject CreateResult(JsonNode? id, JsonObject result) =>
    new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    };

static JsonObject CreateError(JsonNode? id, string message) =>
    new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = -32600,
            ["message"] = message
        }
    };
