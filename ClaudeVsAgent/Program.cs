using System.Text.Json;

try
{
    Console.WriteLine("READY");
    Console.Out.Flush();

    while (true)
    {
        var line = Console.ReadLine();

        if (line == null)
            break;

        if (string.IsNullOrWhiteSpace(line))
            continue;

        var request = JsonSerializer.Deserialize<ChatRequest>(line);

        var response = new ChatResponse
        {
            Text = $"Agent recebeu: {request?.Message}"
        };

        var json = JsonSerializer.Serialize(response);

        Console.WriteLine(json);
        Console.Out.Flush();
    }
}
catch (Exception ex)
{
    Console.WriteLine(JsonSerializer.Serialize(new ChatResponse
    {
        Text = ex.ToString()
    }));

    Console.Out.Flush();
}

public class ChatRequest
{
    public string Message { get; set; } = "";
}

public class ChatResponse
{
    public string Text { get; set; } = "";
}