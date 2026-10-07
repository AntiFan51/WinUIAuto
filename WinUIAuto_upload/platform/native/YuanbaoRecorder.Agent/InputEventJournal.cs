using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace YuanbaoRecorder.Agent
{
    internal sealed class InputEventJournal : IDisposable
    {
        private readonly object sync = new object();
        private StreamWriter writer;
        private int sequence;

        internal InputEventJournal(string filePath)
        {
            writer = new StreamWriter(filePath, false, new UTF8Encoding(false)) { AutoFlush = true };
        }

        internal string Record(string eventType, DateTime timestampUtc, int x, int y, bool controlPressed, string disposition)
        {
            lock (sync)
            {
                if (writer == null) return null;
                sequence++;
                var eventId = "event_" + sequence.ToString("D6", CultureInfo.InvariantCulture);
                writer.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{{\"id\":\"{0}\",\"timestamp\":\"{1}\",\"type\":\"{2}\",\"x\":{3},\"y\":{4},\"ctrl\":{5},\"disposition\":\"{6}\"}}",
                    eventId,
                    timestampUtc.ToString("O"),
                    Escape(eventType),
                    x,
                    y,
                    controlPressed ? "true" : "false",
                    Escape(disposition)));
                return eventId;
            }
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (writer == null) return;
                writer.Dispose();
                writer = null;
            }
        }
    }
}
