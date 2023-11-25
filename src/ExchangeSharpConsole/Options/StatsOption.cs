using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using ExchangeSharp;
using ExchangeSharpConsole.Options.Interfaces;

namespace ExchangeSharpConsole.Options
{
	[Verb(
			"stats",
			HelpText = "Show stats from 4 exchanges.\n"
					+ "This is a great way to see the price, order book and other useful stats."
	)]
	public class StatsOption : BaseOption, IOptionPerExchange
	{
		public override async Task RunCommand()
		{
			var marketSymbol = "BTC-USD";

			Console.WriteLine("butts");
			var apiCoinbase = await ExchangeAPI.GetExchangeAPIAsync(ExchangeName);

			Authenticate(apiCoinbase);

			//TODO: Make this multi-threaded and add parameters
			Console.WriteLine("Use CTRL-C to stop.");

			while (true)
			{
				var tickers = await apiCoinbase.GetTickersAsync();
				Console.WriteLine("Get the tickers", tickers);
				var orders = await apiCoinbase.GetOrderBookAsync(marketSymbol);
				var askAmountSum = orders.Asks.Values.Sum(o => o.Amount);
				var askPriceSum = orders.Asks.Values.Sum(o => o.Price);
				var bidAmountSum = orders.Bids.Values.Sum(o => o.Amount);
				var bidPriceSum = orders.Bids.Values.Sum(o => o.Price);

				Console.Clear();
				Console.WriteLine(
						"GDAX: {0,13:N}, {1,15:N}, {2,8:N}, {3,13:N}, {4,8:N}, {5,13:N}",
						tickers.Count(),
						tickers.First().Value.Volume.QuoteCurrencyVolume,
						askAmountSum,
						askPriceSum,
						bidAmountSum,
						bidPriceSum
				);

				Thread.Sleep(IntervalMs);
			}
		}

		public int IntervalMs { get; set; }
		public string ExchangeName { get; set; }
	}
}
