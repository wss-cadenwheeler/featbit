using Domain.Messages;

namespace Infrastructure.MQ;

public class ClientConnectionMadeHandler : IMessageHandler
{
    public string Topic => Topics.ConnectionMade;

    public async Task HandleAsync(string message)
    {
        Console.WriteLine($"Handling connection made message: {message}");
    }
}
