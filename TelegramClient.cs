using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slip;

internal abstract record TelegramEvent;

internal sealed record TextMessage(long UserId, string? Username, long ChatId, string Text) : TelegramEvent;

internal sealed record ButtonPress(
    long UserId, string? Username, long ChatId, int MessageId, string CallbackId, string Data) : TelegramEvent;

internal readonly record struct Button(string Text, string Data);

internal sealed class TelegramClient
{
    private readonly string _token;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(35) };

    public TelegramClient(string token) => _token = token;

    public async Task<bool> ValidateAsync()
    {
        try
        {
            var resp = await _http.GetStringAsync($"https://api.telegram.org/bot{_token}/getMe");
            using var doc = JsonDocument.Parse(resp);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    public Task SendMessageAsync(long chatId, string text, IReadOnlyList<IReadOnlyList<Button>>? keyboard = null) =>
        PostAsync("sendMessage", new
        {
            chat_id = chatId,
            text,
            parse_mode = "HTML",
            reply_markup = ToMarkup(keyboard),
        });

    public Task EditMessageTextAsync(
        long chatId, int messageId, string text, IReadOnlyList<IReadOnlyList<Button>>? keyboard = null) =>
        PostAsync("editMessageText", new
        {
            chat_id = chatId,
            message_id = messageId,
            text,
            parse_mode = "HTML",
            reply_markup = ToMarkup(keyboard),
        });

    public Task AnswerCallbackQueryAsync(string callbackId, string? toast = null) =>
        PostAsync("answerCallbackQuery", new { callback_query_id = callbackId, text = toast });

    private static object? ToMarkup(IReadOnlyList<IReadOnlyList<Button>>? keyboard)
    {
        if (keyboard is null) return null;
        return new
        {
            inline_keyboard = keyboard
                .Select(row => row.Select(b => new { text = b.Text, callback_data = b.Data }).ToArray())
                .ToArray()
        };
    }

    private async Task PostAsync(string method, object payload)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{_token}/{method}";
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            await _http.PostAsync(url, content);
        }
        catch
        {
            // best effort - a dropped call shouldn't crash the daemon
        }
    }

    /// <summary>
    /// Long-polls getUpdates forever (until cancelled), yielding a TelegramEvent
    /// per incoming text message or button press. Network errors are swallowed
    /// and retried after a short delay - this is meant to run unattended.
    /// </summary>
    public async IAsyncEnumerable<TelegramEvent> PollUpdatesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        long offset = 0;

        while (!ct.IsCancellationRequested)
        {
            string resp;
            try
            {
                var url = $"https://api.telegram.org/bot{_token}/getUpdates?offset={offset}&timeout=30";
                resp = await _http.GetStringAsync(url, ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            var batch = new List<TelegramEvent>();
            try
            {
                using var doc = JsonDocument.Parse(resp);
                foreach (var update in doc.RootElement.GetProperty("result").EnumerateArray())
                {
                    offset = update.GetProperty("update_id").GetInt64() + 1;

                    if (update.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("from", out var from) &&
                        msg.TryGetProperty("chat", out var chat))
                    {
                        var userId = from.GetProperty("id").GetInt64();
                        var username = from.TryGetProperty("username", out var u) ? u.GetString() : null;
                        var chatId = chat.GetProperty("id").GetInt64();
                        var text = msg.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                        batch.Add(new TextMessage(userId, username, chatId, text));
                    }
                    else if (update.TryGetProperty("callback_query", out var cb) &&
                             cb.TryGetProperty("from", out var cbFrom) &&
                             cb.TryGetProperty("message", out var cbMsg))
                    {
                        var userId = cbFrom.GetProperty("id").GetInt64();
                        var username = cbFrom.TryGetProperty("username", out var u2) ? u2.GetString() : null;
                        var chatId = cbMsg.GetProperty("chat").GetProperty("id").GetInt64();
                        var messageId = cbMsg.GetProperty("message_id").GetInt32();
                        var callbackId = cb.GetProperty("id").GetString() ?? "";
                        var data = cb.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
                        batch.Add(new ButtonPress(userId, username, chatId, messageId, callbackId, data));
                    }
                }
            }
            catch
            {
                continue;
            }

            foreach (var item in batch) yield return item;
        }
    }
}
