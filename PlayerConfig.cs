using System.Text.Json.Serialization;

namespace SimplePlayer
{
    /// <summary>
    /// Data model for persisting player state to config.json.
    /// Uses System.Text.Json for serialization.
    /// </summary>
    public class PlayerConfig
    {
        /// <summary>
        /// List of absolute file paths for all songs in the playlist.
        /// </summary>
        [JsonPropertyName("playlist")]
        public List<string> Playlist { get; set; } = new();

        /// <summary>
        /// The playback mode: 0 = Sequential, 1 = Shuffle, 2 = SingleLoop.
        /// Stored as int for JSON simplicity and forward compatibility.
        /// </summary>
        [JsonPropertyName("playMode")]
        public int PlayModeValue { get; set; }

        /// <summary>
        /// Volume level from 0.0 (mute) to 1.0 (max).
        /// </summary>
        [JsonPropertyName("volume")]
        public double Volume { get; set; } = 0.5;

        /// <summary>
        /// UI language code: "zh-CN" or "en-US". Defaults to Chinese.
        /// </summary>
        [JsonPropertyName("language")]
        public string Language { get; set; } = "zh-CN";
    }
}
