using System.Text.RegularExpressions;

namespace WhatsAppLab.Application;

/// <summary>
/// Turns a message template into the text that is really sent, so repeated test messages are not identical.
/// Tokens (everything else is left as written):
///   {time}      server local time, HH:mm:ss
///   {date}      yyyy-MM-dd
///   {datetime}  yyyy-MM-dd HH:mm:ss
///   {rand}      4 random digits
///   {a|b|c}     one of the options, picked at random
/// Applied when the message is put in the outbox (the API and the Hangfire job), so every run of a recurring announcement gets its own text
/// and the outbox keeps the text that was actually sent.
/// </summary>
public static class MessageText
{
    private static readonly Regex Token = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

    public static string Render(string template) => Render(template, DateTime.Now, Random.Shared);

    public static string Render(string template, DateTime now, Random random)
    {
        if (string.IsNullOrEmpty(template)) return template;
        return Token.Replace(template, m =>
        {
            var t = m.Groups[1].Value.Trim();
            switch (t.ToLowerInvariant())
            {
                case "time": return now.ToString("HH:mm:ss");
                case "date": return now.ToString("yyyy-MM-dd");
                case "datetime": return now.ToString("yyyy-MM-dd HH:mm:ss");
                case "rand": return random.Next(0, 10000).ToString("D4");
            }
            if (t.Contains('|'))
            {
                var options = t.Split('|').Select(o => o.Trim()).Where(o => o.Length > 0).ToArray();
                if (options.Length > 0) return options[random.Next(options.Length)];
            }
            return m.Value;   // not a token: keep the braces as typed
        });
    }
}
