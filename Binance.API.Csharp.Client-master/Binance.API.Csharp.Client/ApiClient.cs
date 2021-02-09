﻿using Binance.API.Csharp.Client.Domain.Abstract;
using Binance.API.Csharp.Client.Domain.Interfaces;
using Binance.API.Csharp.Client.Models.Enums;
using Binance.API.Csharp.Client.Models.WebSocket;
using Binance.API.Csharp.Client.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using WebSocketSharp;

namespace Binance.API.Csharp.Client
{
    public class ApiClient : ApiClientAbstract, IApiClient
    {

        /// <summary>
        /// ctor.
        /// </summary>
        /// <param name="apiKey">Key used to authenticate within the API.</param>
        /// <param name="apiSecret">API secret used to signed API calls.</param>
        /// <param name="apiUrl">API base url.</param>
        public ApiClient(string apiKey, string apiSecret, string apiUrl = @"https://www.binance.com", string webSocketEndpoint = @"wss://stream.binance.com:9443/ws/", bool addDefaultHeaders = true) : base(apiKey, apiSecret, apiUrl, webSocketEndpoint, addDefaultHeaders)
        {
        }

        /// <summary>
        /// Calls API Methods.
        /// </summary>
        /// <typeparam name="T">Type to which the response content will be converted.</typeparam>
        /// <param name="method">HTTPMethod (POST-GET-PUT-DELETE)</param>
        /// <param name="endpoint">Url endpoing.</param>
        /// <param name="isSigned">Specifies if the request needs a signature.</param>
        /// <param name="parameters">Request parameters.</param>
        /// <returns></returns>
        public async Task<T> CallAsync<T>(ApiMethod method, string endpoint, bool isSigned = false, string parameters = null)
        {
            string finalEndpoint = endpoint + (string.IsNullOrWhiteSpace(parameters) ? "" : $"?{parameters}");

            if (isSigned)
            {
                // Joining provided parameters
                parameters += (!string.IsNullOrWhiteSpace(parameters) ? "&timestamp=" : "timestamp=") + Utilities.GenerateTimeStamp(DateTime.Now.ToUniversalTime());

                // Creating request signature
                string signature = Utilities.GenerateSignature(_apiSecret, parameters);
                finalEndpoint = $"{endpoint}?{parameters}&signature={signature}";
            }

            HttpRequestMessage request = new HttpRequestMessage(Utilities.CreateHttpMethod(method.ToString()), finalEndpoint);
            HttpResponseMessage response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                // Api return is OK
                response.EnsureSuccessStatusCode();

                // Get the result
                string result = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                // Serialize and return result
                return JsonConvert.DeserializeObject<T>(result);
            }

            // We received an error
            if (response.StatusCode == HttpStatusCode.GatewayTimeout)
            {
                throw new Exception("Api Request Timeout.");
            }

            // Get te error code and message
            string e = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            // Error Values
            int eCode = 0;
            string eMsg = "";
            if (e.IsValidJson())
            {
                try
                {
                    JObject i = JObject.Parse(e);

                    eCode = i["code"]?.Value<int>() ?? 0;
                    eMsg = i["msg"]?.Value<string>();
                }
                catch { }
            }

            throw new Exception(string.Format("Api Error Code: {0} Message: {1}", eCode, eMsg));
        }

        /// <summary>
        /// Connects to a Websocket endpoint.
        /// </summary>
        /// <typeparam name="T">Type used to parsed the response message.</typeparam>
        /// <param name="parameters">Paremeters to send to the Websocket.</param>
        /// <param name="messageDelegate">Deletage to callback after receive a message.</param>
        /// <param name="useCustomParser">Specifies if needs to use a custom parser for the response message.</param>
        public string ConnectToWebSocket<T>(string parameters, MessageHandler<T> messageHandler, Action<string> openHandler, bool useCustomParser = false)
        {
            string finalEndpoint = _webSocketEndpoint + parameters;

            WebSocket ws = new WebSocket(finalEndpoint);
            string sid = ws.GetHashCode().ToString();

            ws.OnMessage += (sender, e) =>
            {
                dynamic eventData;

                if (useCustomParser)
                {
                    CustomParser customParser = new CustomParser();
                    var test = JsonConvert.DeserializeObject<dynamic>(e.Data);
                    eventData = customParser.GetParsedDepthMessage(test);
                }
                else
                {
                    eventData = JsonConvert.DeserializeObject<T>(e.Data);
                }
                messageHandler(eventData);
            };

            ws.OnOpen += (sender, e) =>
            {
                _openSockets.Add(sid, ws);
                //openHandler(sid);
            };

            ws.OnClose += (sender, e) =>
            {
                //throw new Exception("on close" + e.Reason + " - " + sender.ToString());
                _openSockets.Remove(sid);
            };

            ws.OnError += (sender, e) =>
            {
                //throw new Exception("on error" + e.Message);
                _openSockets.Remove(sid);
            };

            ws.Connect();
            return sid;
        }

        /// <summary>
        /// Connects to a UserData Websocket endpoint.
        /// </summary>
        /// <param name="parameters">Paremeters to send to the Websocket.</param>
        /// <param name="accountHandler">Deletage to callback after receive a account info message.</param>
        /// <param name="tradeHandler">Deletage to callback after receive a trade message.</param>
        /// <param name="orderHandler">Deletage to callback after receive a order message.</param>
        public void ConnectToUserDataWebSocket(string parameters, MessageHandler<AccountUpdatedMessage> accountHandler, MessageHandler<OrderOrTradeUpdatedMessage> tradeHandler, MessageHandler<OrderOrTradeUpdatedMessage> orderHandler)
        {
            string finalEndpoint = _webSocketEndpoint + parameters;

            WebSocket ws = new WebSocket(finalEndpoint);
            string sid = ws.GetHashCode().ToString();

            ws.OnMessage += (sender, e) =>
            {
                dynamic eventData = JsonConvert.DeserializeObject<dynamic>(e.Data);

                switch (eventData.e)
                {
                    case "outboundAccountInfo":
                        accountHandler(JsonConvert.DeserializeObject<AccountUpdatedMessage>(e.Data));
                        break;
                    case "executionReport":
                        bool isTrade = ((string)eventData.x).ToLower() == "trade";

                        if (isTrade)
                        {
                            tradeHandler(JsonConvert.DeserializeObject<OrderOrTradeUpdatedMessage>(e.Data));
                        }
                        else
                        {
                            orderHandler(JsonConvert.DeserializeObject<OrderOrTradeUpdatedMessage>(e.Data));
                        }
                        break;
                }
            };

            ws.OnClose += (sender, e) =>
            {
                _openSockets.Remove(sid);
            };

            ws.OnError += (sender, e) =>
            {
                _openSockets.Remove(sid);
            };

            ws.Connect();
        }

        public bool CloseWebSocket(string WebSocketID)
        {
            if (_openSockets.ContainsKey(WebSocketID))
            {
                _openSockets[WebSocketID].Close();
                return true;
            }
            else
            {
                return false;
            }
        }

        public bool RestartWebSocket(string WebSocketID)
        {
            if (_openSockets.ContainsKey(WebSocketID))
            {
                WebSocket tws = _openSockets[WebSocketID];
                tws.Close();
                tws.Connect();
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}
