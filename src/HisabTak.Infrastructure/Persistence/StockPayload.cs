using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HisabTak.Infrastructure.Persistence;

internal static class StockPayload
{
    private sealed class DecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>reader.GetDecimal();
        public override void Write(Utf8JsonWriter writer,decimal value,JsonSerializerOptions options)=>
            writer.WriteRawValue(value.ToString("G29",CultureInfo.InvariantCulture));
    }
    private static readonly JsonSerializerOptions Options=new(){Converters={new DecimalConverter()}};
    public static string Encode<T>(T value)=>JsonSerializer.Serialize(value,Options);
}
