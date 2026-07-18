using System.Text.Json.Serialization;

namespace Finder.Models.Telegram
{
    public class TelegramGetUpdatesResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("result")]
        public List<TelegramUpdate> Result { get; set; } = new();
    }

    public class TelegramUpdate
    {
        [JsonPropertyName("update_id")]
        public long UpdateId { get; set; }

        [JsonPropertyName("message")]
        public TelegramMessage? Message { get; set; }
    }

    public class TelegramMessage
    {
        [JsonPropertyName("message_id")]
        public long MessageId { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("chat")]
        public TelegramChat Chat { get; set; } = new();
    }

    public class TelegramChat
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
    }
}