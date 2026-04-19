using System.Threading.Channels;
using UrlShortener.Api.Models;

namespace UrlShortener.Api.Services;

public class ClickQueue
{
    private readonly Channel<ClickEvent> _channel = Channel.CreateUnbounded<ClickEvent>();

    public ChannelReader<ClickEvent> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(ClickEvent ev) => _channel.Writer.WriteAsync(ev);
}
