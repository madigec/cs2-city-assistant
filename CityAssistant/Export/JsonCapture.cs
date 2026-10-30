using System.Collections.Generic;
using Colossal.UI.Binding;

namespace CityAssistant.Export
{
    /// <summary>
    /// Catches what a game panel would send to the UI and keeps it as our own JSON tree.
    /// Some panels (transport lines, building levels, demand factors, ...) don't store their numbers in
    /// fields; they write them straight into the UI's JSON writer. Handing them this writer instead
    /// gives us exactly the data the panel shows.
    /// Game "types" become objects (with their type name under "type" only when they have no properties),
    /// maps become objects, arrays become lists.
    /// </summary>
    public class JsonCapture : IJsonWriter
    {
        // One open object, map or array; a map is written as key, value, key, value.
        private class Frame
        {
            public object Container;
            public string NameInParent;
            public string TypeName;
            public bool IsMap;
            public string MapKey;
        }

        private readonly Stack<Frame> m_Open = new Stack<Frame>();
        private string m_PropertyName;
        private bool m_HasResult;

        public object Result { get; private set; }

        public string debugName => nameof(JsonCapture);

        public static object Capture(System.Action<IJsonWriter> write)
        {
            var capture = new JsonCapture();
            write(capture);
            return capture.Result;
        }

        public static object Capture(IJsonWritable value) => Capture(value.Write);

        public void TypeBegin(string name) => Open(new JsonObject()).TypeName = name;

        public void TypeEnd()
        {
            var frame = m_Open.Peek();
            var obj = (JsonObject)frame.Container;
            // A type without properties (e.g. an empty marker) still says what it was.
            if (obj.Count == 0 && frame.TypeName != null)
                obj.Add("type", ShortTypeName(frame.TypeName));
            Close();
        }

        public void MapBegin(uint size) => Open(new JsonObject()).IsMap = true;

        public void MapEnd() => Close();

        public void ArrayBegin(uint size) => Open(new List<object>((int)size));

        public void ArrayEnd() => Close();

        public void PropertyName(string name) => m_PropertyName = name;

        public void WriteNull() => Value(null);
        public void Write(bool value) => Value(value);
        public void Write(int value) => Value(value);
        public void Write(uint value) => Value(value);
        public void Write(long value) => Value(value);
        public void Write(ulong value) => Value(value);
        public void Write(float value) => Value(value);
        public void Write(double value) => Value(value);
        public void Write(string value) => Value(value);

        private Frame Open(object container)
        {
            var frame = new Frame { Container = container, NameInParent = m_PropertyName };
            m_PropertyName = null;
            m_Open.Push(frame);
            return frame;
        }

        private void Close()
        {
            var frame = m_Open.Pop();
            m_PropertyName = frame.NameInParent;
            Value(frame.Container);
        }

        private void Value(object value)
        {
            if (m_Open.Count == 0)
            {
                if (!m_HasResult)
                {
                    Result = value;
                    m_HasResult = true;
                }
                return;
            }

            var frame = m_Open.Peek();
            switch (frame.Container)
            {
                case JsonObject obj when frame.IsMap:
                    if (frame.MapKey == null)
                    {
                        frame.MapKey = value?.ToString() ?? "null";
                    }
                    else
                    {
                        obj.Add(frame.MapKey, value);
                        frame.MapKey = null;
                    }
                    break;
                case JsonObject obj:
                    obj.Add(m_PropertyName ?? $"_{obj.Count}", value);
                    m_PropertyName = null;
                    break;
                case List<object> list:
                    list.Add(value);
                    break;
            }
        }

        /// <summary>"Game.UI.InGame.TransportInfoviewUISystem+TransportSummaries" -> "TransportSummaries"</summary>
        private static string ShortTypeName(string name)
        {
            var cut = name.LastIndexOfAny(new[] { '.', '+' });
            return cut >= 0 ? name.Substring(cut + 1) : name;
        }
    }
}
