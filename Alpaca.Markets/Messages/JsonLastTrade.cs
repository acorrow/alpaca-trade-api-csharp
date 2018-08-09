﻿using System;
using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace Alpaca.Markets
{
    internal sealed class JsonLastTrade : ILastTrade
    {
        internal sealed class Last
        {
            [JsonProperty(PropertyName = "exchange", Required = Required.Always)]
            public Int64 Exchange { get; set; }

            [JsonProperty(PropertyName = "price", Required = Required.Always)]
            public Decimal Price { get; set; }

            [JsonProperty(PropertyName = "size", Required = Required.Always)]
            public Int64 Size { get; set; }

            [JsonProperty(PropertyName = "timestamp", Required = Required.Always)]
            public Int64 Timestamp { get; set; }
        }

        [JsonProperty(PropertyName = "last", Required = Required.Always)]
        public Last Nested { get; set; }

        [JsonProperty(PropertyName = "status", Required = Required.Always)]
        public String Status { get; set; }

        [JsonProperty(PropertyName = "symbol", Required = Required.Always)]
        public String Symbol { get; set; }

        [JsonIgnore]
        public Int64 Exchange => Nested.Exchange;

        [JsonIgnore]
        public Decimal Price => Nested.Price;

        [JsonIgnore]
        public Int64 Size => Nested.Size;

        [JsonIgnore]
        public DateTime Time { get; private set; }

        [OnDeserialized]
        internal void OnDeserializedMethod(
            StreamingContext context)
        {
#if NET45
            Time = DateTimeHelper.FromUnixTimeMilliseconds(Nested.Timestamp);
#else
            Time = DateTime.SpecifyKind(
                DateTimeOffset.FromUnixTimeMilliseconds(Nested.Timestamp)
                    .DateTime, DateTimeKind.Utc);
#endif
        }
    }
}