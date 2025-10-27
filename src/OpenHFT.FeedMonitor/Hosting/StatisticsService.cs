using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Feed.Models;

namespace OpenHFT.FeedMonitor.Hosting;

public class StatisticsService : IHostedService, IDisposable
{
    private readonly ILogger<StatisticsService> _logger;
    private readonly Feed.FeedMonitor _feedMonitor; // FeedMonitor를 주입받음
    private Timer? _timer;

    public StatisticsService(ILogger<StatisticsService> logger, Feed.FeedMonitor feedMonitor)
    {
        _logger = logger;
        _feedMonitor = feedMonitor;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Statistics Service is starting.");
        _timer = new Timer(PrintStatistics, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }

    private void PrintStatistics(object? state)
    {
        var statisticsField = typeof(Feed.FeedMonitor).GetField("_statistics", BindingFlags.NonPublic | BindingFlags.Instance);
        if (statisticsField?.GetValue(_feedMonitor) is not ConcurrentDictionary<ExchangeEnum, ConcurrentDictionary<ProductType, ConcurrentDictionary<int, FeedStatistics>>> statsDict)
        {
            return;
        }

        var sb = new StringBuilder();
        BuildStatisticsString(sb, statsDict);
        _logger.LogInformationWithCaller($"Feed Statistics Update:\n{sb.ToString()}");
    }

    private void BuildStatisticsString(StringBuilder sb, ConcurrentDictionary<ExchangeEnum, ConcurrentDictionary<ProductType, ConcurrentDictionary<int, FeedStatistics>>> statsDict)
    {
        sb.AppendLine("\n--- Feed Statistics ---");
        sb.AppendLine($"Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC");

        var offsets = TimeSync.GetAllOffsetsMillis();
        if (offsets.Any())
        {
            var offsetStrings = string.Join(", ", offsets.Select(kvp => $"{kvp.Key}: {kvp.Value}ms"));
            sb.AppendLine($"Time Offsets: {offsetStrings}");
        }
        sb.AppendLine("---------------------------------------------------------------------------------------------------------------------------------");
        sb.AppendLine("| Exchange         | Product Type     | Topic            | Msgs/sec | Avg Latency(ms) | P95 Latency(ms) | Gaps | Drop(%)  | Reconnects |");
        sb.AppendLine("---------------------------------------------------------------------------------------------------------------------------------");


        if (statsDict.IsEmpty)
        {
            sb.AppendLine("| No data available yet...                                                                        |");
        }
        else
        {
            foreach (var exchangePair in statsDict.OrderBy(p => p.Key))
            {
                foreach (var productPair in exchangePair.Value.OrderBy(p => p.Key))
                {
                    foreach (var topicPair in productPair.Value.OrderBy(p => p.Key))
                    {
                        var topicId = topicPair.Key;
                        var stats = topicPair.Value;
                        var topicName = TopicRegistry.TryGetTopic(topicId, out var topic) ? topic.GetTopicName() : $"Unknown({topicId})";

                        sb.Append($"| {exchangePair.Key,-16} | {productPair.Key,-16} | {topicName,-16} ");
                        sb.Append($"| {stats.MessagesPerSecond,-8:F0} ");
                        sb.Append($"| {stats.AvgE2ELatency,-15:F2} ");
                        sb.Append($"| {stats.GetLatencyPercentile(0.95),-15:F2} ");
                        sb.Append($"| {stats.SequenceGaps,-4} ");
                        sb.Append($"| {stats.DropRate,-8:P2} ");
                        sb.Append($"| {stats.ReconnectCount,-10} |");
                        sb.AppendLine();
                    }
                }
            }
        }
        sb.AppendLine("---------------------------------------------------------------------------------------------------------------------------------");

    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Statistics Service is stopping.");
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();
}