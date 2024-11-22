using Alpaca.Markets;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace UsageExamples;

// This version of the mean reversion example algorithm utilizes Alpaca data that
// is available to users who have a funded Alpaca brokerage account. By default, it
// is configured to use the paper trading API, but you can change it to use the live
// trading API by setting the API_URL.
[SuppressMessage("ReSharper", "UnusedVariable")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "RedundantDefaultMemberInitializer")]
[JsonSerializable(typeof(List<Decimal>))]
internal sealed partial class MeanReversionWithCryptoJsonContext : JsonSerializerContext
{
    public MeanReversionWithCryptoJsonContext() : base(new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    })
    {
    }

    public override JsonTypeInfo GetTypeInfo(Type type)
    {
        if (type == typeof(List<Decimal>))
        {
            return MeanReversionWithCryptoJsonContext.Default.ListDecimal;
        }
        throw new NotSupportedException($"Type '{type}' is not supported by this context.");
    }
}

internal sealed class MeanReversionWithCrypto : IDisposable
{
    private const String API_KEY = "REPLACEME";
    private const String API_SECRET = "REPLACEME";
    private static readonly IConfiguration config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .Build();
    private static readonly String[] symbols = config.GetSection("Symbols").GetChildren().Select(x => x.Value).ToArray();
    private static readonly Decimal blockSize = Decimal.Parse(config["BlockSize"] ?? "10");
    private const Decimal scale = 200;
    private const int maxRetryAttempts = 5;
    private static readonly TimeSpan initialDelay = TimeSpan.FromSeconds(5);
    private const string closingPricesFilePath = "closingPrices.json";
    private static readonly Mutex fileMutex = new Mutex(false, "MeanReversionWithCryptoFileMutex");

    private IAlpacaCryptoDataClient alpacaCryptoDataClient;
    private IAlpacaTradingClient alpacaTradingClient;
    private IAlpacaStreamingClient alpacaStreamingClient;
    private IAlpacaCryptoStreamingClient alpacaCryptoStreamingClient;
    private readonly ILogger<MeanReversionWithCrypto> logger;

    private Guid lastTradeId = Guid.NewGuid();
    private Boolean lastTradeOpen;
    private readonly List<Decimal> closingPrices = new();
    private Boolean isAssetShortable;

    public MeanReversionWithCrypto(ILogger<MeanReversionWithCrypto> logger)
    {
        this.logger = logger;
    }

    public async Task run(CancellationToken cancellationToken)
    {
        try
        {
            alpacaTradingClient = Environments.Paper.GetAlpacaTradingClient(new SecretKey(API_KEY, API_SECRET));
            alpacaCryptoDataClient = Environments.Paper.GetAlpacaCryptoDataClient(new SecretKey(API_KEY, API_SECRET));
            alpacaStreamingClient = Environments.Paper.GetAlpacaStreamingClient(new SecretKey(API_KEY, API_SECRET));

            await alpacaStreamingClient.ConnectAndAuthenticateAsync(cancellationToken);
            alpacaStreamingClient.OnTradeUpdate += handleTradeUpdate;

            foreach (var symbol in symbols)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    logger.LogInformation("Cancellation requested. Exiting run method.");
                    return;
                }

                var asset = await alpacaTradingClient.GetAssetAsync(symbol, cancellationToken);
                isAssetShortable = asset.Shortable;

                await alpacaTradingClient.CancelAllOrdersAsync(cancellationToken);

                var today = DateTime.Today;
                var calendar = await alpacaTradingClient.ListIntervalCalendarAsync(
                    CalendarRequest.GetForSingleDay(DateOnly.FromDateTime(today)), cancellationToken);
                var tradingDay = calendar[0];

                var bars = await alpacaCryptoDataClient.ListHistoricalBarsAsync(
                    new HistoricalCryptoBarsRequest(symbol, BarTimeFrame.Minute, tradingDay.Trading), cancellationToken);
                var lastBars = bars.Items.Skip(Math.Max(0, bars.Items.Count - 20));

                foreach (var bar in lastBars)
                {
                    if (bar.TimeUtc.Date == today)
                    {
                        closingPrices.Add(bar.Close);
                    }
                }

                Console.WriteLine("Waiting for market open...");
                await awaitMarketOpen(cancellationToken);
                Console.WriteLine("Market opened.");

                alpacaCryptoStreamingClient = Environments.Live.GetAlpacaCryptoStreamingClient(new SecretKey(API_KEY, API_SECRET));

                await connectWithRetryAsync(symbol, cancellationToken);

                var subscription = alpacaCryptoStreamingClient.GetMinuteBarSubscription(symbol);
                subscription.Received += async bar => await handleMinuteBar(bar, symbol, cancellationToken);
                await alpacaCryptoStreamingClient.SubscribeAsync(subscription, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Operation canceled.");
        }
        catch (AlpacaRestException ex)
        {
            logger.LogError(ex, "Alpaca REST API error occurred while running the mean reversion strategy.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An unexpected error occurred while running the mean reversion strategy.");
        }
        finally
        {
            await closePositionAtMarket(cancellationToken);
            Dispose();
        }
    }

    private async Task connectWithRetryAsync(string symbol, CancellationToken cancellationToken)
    {
        int attempt = 0;
        TimeSpan delay = initialDelay;
        var connectionId = Guid.NewGuid();

        while (attempt < maxRetryAttempts)
        {
            try
            {
                logger.LogInformation("Connection ID {ConnectionId}: Attempting to connect to Alpaca streaming client for symbol {Symbol} at {Time}. Attempt {Attempt} of {MaxAttempts}.", connectionId, symbol, DateTime.UtcNow, attempt + 1, maxRetryAttempts);
                await alpacaCryptoStreamingClient.ConnectAndAuthenticateAsync(cancellationToken);
                logger.LogInformation("Connection ID {ConnectionId}: Successfully connected to Alpaca streaming client for symbol {Symbol} at {Time}.", connectionId, symbol, DateTime.UtcNow);
                return;
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Connection attempt canceled.");
                throw;
            }
            catch (AlpacaRestException ex)
            {
                attempt++;
                logger.LogError(ex, "Connection ID {ConnectionId}: Alpaca REST API error while connecting to streaming client for symbol {Symbol} at {Time}. Attempt {Attempt} of {MaxAttempts}. Retrying in {Delay} seconds...", connectionId, symbol, DateTime.UtcNow, attempt, maxRetryAttempts, delay.TotalSeconds);
                if (attempt >= maxRetryAttempts)
                {
                    logger.LogError("Connection ID {ConnectionId}: Max retry attempts reached. Unable to connect to Alpaca streaming client for symbol {Symbol} at {Time}.", connectionId, symbol, DateTime.UtcNow);
                    throw;
                }
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2); // Exponential backoff
            }
            catch (Exception ex)
            {
                attempt++;
                logger.LogError(ex, "Connection ID {ConnectionId}: Unexpected error while connecting to streaming client for symbol {Symbol} at {Time}. Attempt {Attempt} of {MaxAttempts}. Retrying in {Delay} seconds...", connectionId, symbol, DateTime.UtcNow, attempt, maxRetryAttempts, delay.TotalSeconds);
                if (attempt >= maxRetryAttempts)
                {
                    logger.LogError("Connection ID {ConnectionId}: Max retry attempts reached. Unable to connect to Alpaca streaming client for symbol {Symbol} at {Time}.", connectionId, symbol, DateTime.UtcNow);
                    throw;
                }
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2); // Exponential backoff
            }
        }
    }

    public void Dispose()
    {
        dispose(true);
        GC.SuppressFinalize(this);
    }

    private void dispose(bool disposing)
    {
        if (disposing)
        {
            alpacaTradingClient?.Dispose();
            alpacaCryptoDataClient?.Dispose();
            alpacaStreamingClient?.Dispose();
            alpacaCryptoStreamingClient?.Dispose();
        }
    }

    private async Task awaitMarketOpen(CancellationToken cancellationToken)
    {
        try
        {
            while (!(await alpacaTradingClient.GetClockAsync(cancellationToken)).IsOpen)
            {
                await Task.Delay(60000, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Market open wait canceled.");
        }
        catch (AlpacaRestException ex)
        {
            logger.LogError(ex, "Alpaca REST API error occurred while waiting for the market to open.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An unexpected error occurred while waiting for the market to open.");
        }
    }

    private async Task handleMinuteBar(IBar agg, string symbol, CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Attempting to acquire file mutex for reading closing prices.");
            fileMutex.WaitOne();
            logger.LogInformation("File mutex acquired for reading closing prices.");

            if (File.Exists(closingPricesFilePath))
            {
                logger.LogInformation("Reading closing prices from file.");
                var json = await File.ReadAllTextAsync(closingPricesFilePath, cancellationToken);
                var savedPrices = JsonSerializer.Deserialize(json, MeanReversionWithCryptoJsonContext.Default.ListDecimal);
                if (savedPrices != null && savedPrices.All(price => price >= 0))
                {
                    closingPrices.AddRange(savedPrices);
                    logger.LogInformation("Closing prices successfully read from file.");
                }
                else
                {
                    logger.LogError("Invalid data found in closing prices file.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Reading closing prices canceled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading closing prices from file. Using default empty list.");
            closingPrices.Clear();
        }
        finally
        {
            fileMutex.ReleaseMutex();
            logger.LogInformation("File mutex released after reading closing prices.");
        }

        closingPrices.Add(agg.Close);
        if (closingPrices.Count <= 20)
        {
            Console.WriteLine("Waiting on more data.");
            return;
        }

        closingPrices.RemoveAt(0);

        int writeAttempts = 0;
        bool writeSuccessful = false;
        while (writeAttempts < maxRetryAttempts && !writeSuccessful)
        {
            try
            {
                logger.LogInformation("Attempting to acquire file mutex for writing closing prices.");
                fileMutex.WaitOne();
                logger.LogInformation("File mutex acquired for writing closing prices.");

                var json = JsonSerializer.Serialize(closingPrices, MeanReversionWithCryptoJsonContext.Default.ListDecimal);
                await File.WriteAllTextAsync(closingPricesFilePath, json, cancellationToken);
                logger.LogInformation("Closing prices successfully written to file.");
                writeSuccessful = true;
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Writing closing prices canceled.");
                break;
            }
            catch (Exception ex)
            {
                writeAttempts++;
                logger.LogError(ex, $"Error saving closing prices to file. Attempt {writeAttempts} of {maxRetryAttempts}.");
                if (writeAttempts >= maxRetryAttempts)
                {
                    logger.LogError("Max retry attempts reached. Unable to save closing prices to file.");
                }
                await Task.Delay(initialDelay, cancellationToken);
            }
            finally
            {
                fileMutex.ReleaseMutex();
                logger.LogInformation("File mutex released after writing closing prices.");
            }
        }

        var avg = closingPrices.Average();
        var diff = avg - agg.Close;

        if (lastTradeOpen)
        {
            try
            {
                await alpacaTradingClient.CancelOrderAsync(lastTradeId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Canceling last trade order canceled.");
            }
            catch (AlpacaRestException ex)
            {
                logger.LogError(ex, "Alpaca REST API error occurred while canceling the last trade order.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An unexpected error occurred while canceling the last trade order.");
            }
        }

        var account = await alpacaTradingClient.GetAccountAsync(cancellationToken);
        var buyingPower = account.BuyingPower * 0.10M ?? 0M;
        var equity = account.Equity;
        var multiplier = (Int64)account.Multiplier;

        var positionQuantity = 0L;
        var positionValue = 0M;
        try
        {
            var currentPosition = await alpacaTradingClient.GetPositionAsync(symbol, cancellationToken);
            positionQuantity = currentPosition.IntegerQuantity;
            positionValue = currentPosition.MarketValue ?? 0M;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Getting current position canceled.");
        }
        catch (AlpacaRestException ex)
        {
            logger.LogError(ex, "Alpaca REST API error occurred while getting the current position.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An unexpected error occurred while getting the current position.");
        }

        if (diff <= 0)
        {
            if (positionQuantity > 0)
            {
                Console.WriteLine($"Removing {positionValue:C2} long position.");
                await submitOrder(positionQuantity, agg.Close, OrderSide.Sell, symbol, cancellationToken);
            }
            else
            {
                var portfolioShare = -1 * diff / agg.Close * scale;
                var targetPositionValue = -1 * equity * multiplier * portfolioShare;
                var amountToShort = targetPositionValue - positionValue;

                switch (amountToShort)
                {
                    case < 0:
                        amountToShort *= -1;
                        if (amountToShort > buyingPower)
                        {
                            amountToShort = buyingPower;
                        }

                        var qty = (Int64)(amountToShort / agg.Close);
                        if (isAssetShortable)
                        {
                            Console.WriteLine($"Adding {qty * agg.Close:C2} to short position.");
                            await submitOrder(qty, agg.Close, OrderSide.Sell, symbol, cancellationToken);
                        }
                        else
                        {
                            Console.WriteLine("Unable to place short order - asset is not shortable.");
                        }
                        break;

                    case > 0:
                        qty = (Int64)(amountToShort / agg.Close);
                        if (qty > -1 * positionQuantity)
                        {
                            qty = -1 * positionQuantity;
                        }

                        Console.WriteLine($"Removing {qty * agg.Close:C2} from short position");
                        await submitOrder(qty, agg.Close, OrderSide.Buy, symbol, cancellationToken);
                        break;
                }
            }
        }
        else
        {
            var portfolioShare = diff / agg.Close * scale;
            var targetPositionValue = equity * multiplier * portfolioShare;
            var amountToLong = targetPositionValue - positionValue;

            if (positionQuantity < 0)
            {
                Console.WriteLine($"Removing {positionValue:C2} short position.");
                await submitOrder(-positionQuantity, agg.Close, OrderSide.Buy, symbol, cancellationToken);
            }
            else switch (amountToLong)
            {
                case > 0:
                    if (amountToLong > buyingPower)
                    {
                        amountToLong = buyingPower;
                    }

                    var qty = (Int32)(amountToLong / agg.Close);

                    await submitOrder(qty, agg.Close, OrderSide.Buy, symbol, cancellationToken);
                    Console.WriteLine($"Adding {qty * agg.Close:C2} to long position.");
                    break;

                case < 0:
                    amountToLong *= -1;
                    qty = (Int32)(amountToLong / agg.Close);
                    if (qty > positionQuantity)
                    {
                        qty = (Int32)positionQuantity;
                    }

                    if (isAssetShortable)
                    {
                        await submitOrder(qty, agg.Close, OrderSide.Sell, symbol, cancellationToken);
                        Console.WriteLine($"Removing {qty * agg.Close:C2} from long position");
                    }
                    else
                    {
                        Console.WriteLine("Unable to place short order - asset is not shortable.");
                    }
                    break;
            }
        }
    }

    private void handleTradeUpdate(ITradeUpdate trade)
    {
        if (trade.Order.OrderId != lastTradeId)
        {
            return;
        }

        switch (trade.Event)
        {
            case TradeEvent.Fill:
                Console.WriteLine("Trade filled.");
                lastTradeOpen = false;
                break;
            case TradeEvent.Rejected:
                Console.WriteLine("Trade rejected.");
                lastTradeOpen = false;
                break;
            case TradeEvent.Canceled:
                Console.WriteLine("Trade canceled.");
                lastTradeOpen = false;
                break;
        }
    }

    private async Task submitOrder(Int64 quantity, Decimal price, OrderSide side, string symbol, CancellationToken cancellationToken)
    {
        if (quantity == 0)
        {
            return;
        }
        try
        {
            var order = await alpacaTradingClient.PostOrderAsync(
                side.Limit(symbol, quantity, price), cancellationToken);

            lastTradeId = order.OrderId;
            lastTradeOpen = true;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Submitting order canceled.");
        }
        catch (AlpacaRestException ex)
        {
            logger.LogError(ex, "Alpaca REST API error occurred while submitting an order.");
        }
        catch (Exception e)
        {
            logger.LogError(e, "An unexpected error occurred while submitting an order.");
        }
    }

    private async Task closePositionAtMarket(CancellationToken cancellationToken)
    {
        foreach (var symbol in symbols)
        {
            try
            {
                var positionQuantity = (await alpacaTradingClient.GetPositionAsync(symbol, cancellationToken)).IntegerQuantity;
                Console.WriteLine("Closing position at market price.");
                if (positionQuantity > 0)
                {
                    await alpacaTradingClient.PostOrderAsync(
                        OrderSide.Sell.Market(symbol, positionQuantity), cancellationToken);
                }
                else
                {
                    await alpacaTradingClient.PostOrderAsync(
                        OrderSide.Buy.Market(symbol, Math.Abs(positionQuantity)), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Closing position at market canceled.");
            }
            catch (AlpacaRestException ex)
            {
                logger.LogError(ex, "Alpaca REST API error occurred while closing the position at market.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An unexpected error occurred while closing the position at market.");
            }
        }
    }
}
