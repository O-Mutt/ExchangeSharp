namespace ExchangeSharp
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Net;
	using System.Threading.Tasks;
	using ExchangeSharp.CoinbasePro;
	using Newtonsoft.Json;
	using Newtonsoft.Json.Linq;

	public sealed partial class ExchangeCoinbaseAPI : ExchangeAPI
	{
		public string ExchangeApiUrl { get; set; } = "https://api.exchange.coinbase.com";
		public override string BaseUrl { get; set; } = "https://api.coinbase.com/api/v3/brokerage";
		public override string BaseUrlWebSocket { get; set; } = "wss://ws-feed.pro.coinbase.com";

		private ExchangeCoinbaseAPI()
		{
			RequestContentType = "application/json";
			NonceStyle = NonceStyle.UnixSeconds;
			NonceEndPoint = "/time";
			NonceEndPointField = "iso";
			NonceEndPointStyle = NonceStyle.Iso8601;
			/* Rate limits from Coinbase Pro webpage
			 * Public endpoints - We throttle public endpoints by IP: 10 requests per second, up to 15 requests per second in bursts. Some endpoints may have custom rate limits.
			 * Private endpoints - We throttle private endpoints by profile ID: 15 requests per second, up to 30 requests per second in bursts. Some endpoints may have custom rate limits.
			 * fills endpoint has a custom rate limit of 10 requests per second, up to 20 requests per second in bursts. */
			RateLimit = new RateGate(9, TimeSpan.FromSeconds(1)); // set to 9 to be safe
		}

		protected override async Task ProcessRequestAsync(
						IHttpWebRequest request,
						Dictionary<string, object> payload
				)
		{
			if (CanMakeAuthenticatedRequest(payload))
			{
				// Coinbase is funny and wants a seconds double for the nonce, weird... we convert it to double and back to string invariantly to ensure decimal dot is used and not comma
				string timestamp = payload["nonce"].ToStringInvariant();
				payload.Remove("nonce");
				string body = CryptoUtility.GetJsonForPayload(payload);
				byte[] secret = CryptoUtility.ToBytesBase64Decode(PrivateApiKey);
				string toHash =
						timestamp
						+ request.Method.ToUpperInvariant()
						+ request.RequestUri.PathAndQuery
						+ body;
				string signatureBase64String = CryptoUtility.SHA256SignBase64(toHash, secret);
				secret = null;
				toHash = null;
				request.AddHeader("CB-ACCESS-KEY", PublicApiKey.ToUnsecureString());
				request.AddHeader("CB-ACCESS-SIGN", signatureBase64String);
				request.AddHeader("CB-ACCESS-TIMESTAMP", timestamp);
				request.AddHeader(
						"CB-ACCESS-PASSPHRASE",
						CryptoUtility.ToUnsecureString(Passphrase)
				);
				if (request.Method == "POST")
				{
					await CryptoUtility.WriteToRequestAsync(request, body);
				}
			}
		}


		protected override async Task<
				IEnumerable<KeyValuePair<string, ExchangeTicker>>
		> OnGetTickersAsync()
		{
			Dictionary<string, ExchangeTicker> tickers = new Dictionary<string, ExchangeTicker>(
					StringComparer.OrdinalIgnoreCase
			);
			System.Threading.ManualResetEvent evt = new System.Threading.ManualResetEvent(false);
			List<string> symbols = (await GetMarketSymbolsAsync()).ToList();

			// stupid Coinbase does not have a one shot API call for tickers outside of web sockets
			using (
					var socket = await GetTickersWebSocketAsync(
							(t) =>
							{
								lock (tickers)
								{
									if (symbols.Count != 0)
									{
										foreach (var kv in t)
										{
											if (!tickers.ContainsKey(kv.Key))
											{
												tickers[kv.Key] = kv.Value;
												symbols.Remove(kv.Key);
											}
										}
										if (symbols.Count == 0)
										{
											evt.Set();
										}
									}
								}
							}
					)
			)
			{
				evt.WaitOne(10000);
				return tickers;
			}
		}

		protected override async Task<IEnumerable<ExchangeTrade>> OnGetRecentTradesAsync(
				string marketSymbol,
				int? limit = null
		)
		{
			//https://docs.pro.coinbase.com/#pagination Coinbase limit is 100, however pagination can return more (4 later)
			int requestLimit = (limit == null || limit < 1 || limit > 1000) ? 1000 : (int)limit;

			string baseUrl =
					"/products/"
					+ marketSymbol.ToUpperInvariant()
					+ "/ticker"
					+ "?limit="
					+ requestLimit;
			JToken trades = await MakeJsonRequestAsync<JToken>(baseUrl);
			List<ExchangeTrade> tradeList = new List<ExchangeTrade>();
			foreach (JToken trade in trades)
			{
				tradeList.Add(
						trade.ParseTrade(
								"size",
								"price",
								"side",
								"time",
								TimestampType.Iso8601UTC,
								"trade_id"
						)
				);
			}
			return tradeList;
		}

		protected override async Task<
				IReadOnlyDictionary<string, ExchangeCurrency>
		> OnGetCurrenciesAsync()
		{
			var currencies = new Dictionary<string, ExchangeCurrency>();
			//TODO check this
			JToken products = await MakeJsonRequestAsync<JToken>("/currencies", ExchangeApiUrl);
			foreach (JToken product in products)
			{
				var currency = new ExchangeCurrency
				{
					Name = product["id"].ToStringUpperInvariant(),
					FullName = product["name"].ToStringInvariant(),
					DepositEnabled = true,
					WithdrawalEnabled = true
				};

				currencies[currency.Name] = currency;
			}

			return currencies;
		}

		protected override async Task<IEnumerable<string>> OnGetMarketSymbolsAsync()
		{
			return (await GetMarketSymbolsMetadataAsync())
				.Where(market => market.IsActive ?? true)
				.Select(market => market.MarketSymbol);
		}

		protected internal override async Task<
			IEnumerable<ExchangeMarket>
		> OnGetMarketSymbolsMetadataAsync()
		{
			var markets = new List<ExchangeMarket>();
			JToken products = await MakeJsonRequestAsync<JToken>("/products");
			foreach (JToken product in products)
			{
				var market = new ExchangeMarket
				{
					MarketSymbol = product["id"].ToStringUpperInvariant(),
					QuoteCurrency = product["quote_currency"].ToStringUpperInvariant(),
					BaseCurrency = product["base_currency"].ToStringUpperInvariant(),
					IsActive = string.Equals(
						product["status"].ToStringInvariant(),
						"online",
						StringComparison.OrdinalIgnoreCase
					),
					MinTradeSize = product["base_min_size"].ConvertInvariant<decimal>(),
					MaxTradeSize = product["base_max_size"].ConvertInvariant<decimal>(),
					PriceStepSize = product["quote_increment"].ConvertInvariant<decimal>(),
					QuantityStepSize = product["base_increment"].ConvertInvariant<decimal>(),
				};
				markets.Add(market);
			}

			return markets;
		}
		
		// protected virtual Task<IEnumerable<string>> OnGetSymbolsAsync();
		// protected virtual Task<IEnumerable<ExchangeMarket>> OnGetSymbolsMetadataAsync();
		// protected virtual Task<ExchangeTicker> OnGetTickerAsync(string symbol);
		// protected virtual Task<ExchangeOrderBook> OnGetOrderBookAsync(string symbol, int maxCount = 100);
		// protected virtual OnGetHistoricalTradesAsync(Func<IEnumerable<ExchangeTrade>, bool> callback, string symbol, DateTime? startDate = null, DateTime? endDate = null);
		// protected virtual Task<ExchangeDepositDetails> OnGetDepositAddressAsync(string symbol, bool forceRegenerate = false);
		// protected virtual Task<IEnumerable<ExchangeTransaction>> OnGetDepositHistoryAsync(string symbol);
		// protected virtual Task<IEnumerable<MarketCandle>> OnGetCandlesAsync(string symbol, int periodSeconds, DateTime? startDate = null, DateTime? endDate = null, int? limit = null);
		// protected virtual Task<Dictionary<string, decimal>> OnGetAmountsAsync();
		// protected virtual Task<Dictionary<string, decimal>> OnGetFeesAsync();
		// protected virtual Task<Dictionary<string, decimal>> OnGetAmountsAvailableToTradeAsync();
		// protected virtual Task<ExchangeOrderResult> OnPlaceOrderAsync(ExchangeOrderRequest order);
		// protected virtual Task<ExchangeOrderResult[]> OnPlaceOrdersAsync(params ExchangeOrderRequest[] order);
		// protected virtual Task<ExchangeOrderResult> OnGetOrderDetailsAsync(string orderId, string symbol = null);
		// protected virtual Task<IEnumerable<ExchangeOrderResult>> OnGetOpenOrderDetailsAsync(string symbol = null);
		// protected virtual Task<IEnumerable<ExchangeOrderResult>> OnGetCompletedOrderDetailsAsync(string symbol = null, DateTime? afterDate = null);
		// protected virtual Task OnCancelOrderAsync(string orderId, string symbol = null);
		// protected virtual Task<ExchangeWithdrawalResponse> OnWithdrawAsync(ExchangeWithdrawalRequest withdrawalRequest);
		// protected virtual Task<Dictionary<string, decimal>> OnGetMarginAmountsAvailableToTradeAsync();
		// protected virtual Task<ExchangeMarginPositionResult> OnGetOpenPositionAsync(string symbol);
		// protected virtual Task<ExchangeCloseMarginPositionResult> OnCloseMarginPositionAsync(string symbol);

		// protected virtual Task<IWebSocket> OnGetTickersWebSocket(Action<IReadOnlyCollection<KeyValuePair<string, ExchangeTicker>>> tickers);
		// protected virtual Task<IWebSocket> OnGetTradesWebSocket(Action<KeyValuePair<string, ExchangeTrade>> callback, params string[] symbols);
		// protected virtual Task<IWebSocket> OnGetDeltaOrderBookWebSocket(Action<ExchangeOrderBook> callback, int maxCount = 20, params string[] symbols);
		// protected virtual Task<IWebSocket> OnGetOrderDetailsWebSocket(Action<ExchangeOrderResult> callback);
		// protected virtual Task<IWebSocket> OnGetCompletedOrderDetailsWebSocket(Action<ExchangeOrderResult> callback);

		// // these generally do not need to be overriden unless your Exchange does something funny or does not use a symbol separator
		// public virtual string NormalizeSymbol(string symbol);
		// public virtual string ExchangeSymbolToGlobalSymbol(string symbol);
		// public virtual string GlobalSymbolToExchangeSymbol(string symbol);
		// public virtual string PeriodSecondsToString(int seconds);

		// protected virtual void OnDispose();

		/// <summary>
		/// Dictionary of key (exchange currency) and value (global currency).
		/// Some exchanges (Yobit for example) use odd names for some currencies like BCC for Bitcoin Cash.
		/// <example><![CDATA[
		/// ExchangeGlobalCurrencyReplacements[typeof(ExchangeYobitAPI)] = new KeyValuePair<string, string>[]
		/// {
		///     new KeyValuePair<string, string>("BCC", "BCH")
		/// };
		/// ]]></example>
		/// </summary>
		protected Dictionary<string, string> ExchangeGlobalCurrencyReplacements = new Dictionary<
				string,
				string
		>(StringComparer.OrdinalIgnoreCase);


		/// <summary>
		/// The type of web socket order book supported
		/// </summary>
		public WebSocketOrderBookType WebSocketOrderBookType { get; protected set; } =
				WebSocketOrderBookType.FullBookFirstThenDeltas;
	}

	// implement this and change the field name and value to the name of your exchange
	public partial class ExchangeName { public const string Coinbase = "Coinbase"; }
}
