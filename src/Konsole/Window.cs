using System;
using System.Collections.Generic;
using System.Linq;
using Konsole.Internal;

namespace Konsole
{
    public partial class Window : IConsole, IPeek
    {
        internal static IConsole _hostConsole;
        public static IConsole HostConsole
        {
            get
            {
                lock (_locker)
                    return _hostConsole ?? (_hostConsole = new ConcurrentWriter());
            }
            set
            {
                lock (_locker)
                    _hostConsole = value;
            }
        }

        public string GetVersion()
        {
            return GetType().Assembly.GetName().Version.ToString();
        }
        public bool OverflowBottom
        {
            get {
                lock (_locker) return CursorTop >= _height;
            }
        }

            

        // these two fields made mutable to avoid overcomplicating the constructor overloads.
        // perhaps there's a simpler way to do this?
        private int _absoluteX;
        private int _absoluteY;

        private readonly int _x;
        private readonly int _y;
        private readonly int _width;
        private readonly int _height;
        private readonly bool _echo;

        // Echo console is a default wrapper around the real Console, that we can swap out during testing. single underscore indicating it's not for general usage.
        private IConsole _console { get; set; }


        private bool _transparent = false;
        internal static object _locker = new object();

        public bool Clipping
        {
            get { lock (_locker) return _clipping; }
        }

        private bool _clipping = false;

        public bool Scrolling
        {
            get { lock (_locker) return _scrolling; }
        }

        private bool _scrolling = true;

        public bool Transparent
        {
            get { lock (_locker) return _transparent; }
        }

        protected readonly Dictionary<int, Row> _lines = new Dictionary<int, Row>();

        private XY _cursor;
        private int _lastLineWrittenTo = -1;



        public Cell this[int x, int y]
        {
            get
            {
                lock (_locker)
                {
                    int row = y > (_height - 1) ? (_height - 1) : y;
                    int col = x > (_width - 1) ? (_width - 1) : x;
                    return _lines[row].Cells[col];
                }
            }
        }

        private XY Cursor
        {
            get { return _cursor; }
            set
            {
                {
                    int x = value.X >= _width ? (_width - 1) : value.X;
                    int y = value.Y > _height ? _height : value.Y;
                    _cursor = new XY(x, y);

                    if (_cursor.Y > _lastLineWrittenTo && _cursor.X != 0) _lastLineWrittenTo = _cursor.Y;
                    if (_cursor.Y > _lastLineWrittenTo && _cursor.X == 0) _lastLineWrittenTo = _cursor.Y - 1;
                }
            }
        }

        ///<summary>
        ///Override (replace) this func if you want to use `new Window()` in a unit test and you're not using a mockConsole (as the host) 
        /// in your unit test that will provide the height and width. I had to add this because when running tests on non build 
        /// server where the build agent does not give you an open console handle, and accessing console.width and height throws invalid handle exception
        /// so needs to be overridden.
        ///</summary>
        ///<returns>
        /// the height and width of the operating system. For OSX windows we actually return height - 1 to avoid writing to the bottom line
        /// in a console window that will cause the window to scroll, regardless of printing (for now, this is a hack but works well and allows us to 
        /// safely draw boxes around the rest of the "whole" window.
        ///</returns>
        public static Func<(int width, int height)> GetHostWidthHeight = () =>
        {
            lock (_locker)
            {
                if (OS.IsOSX())
                {
                    return (Console.WindowWidth, Console.WindowHeight - 1);
                }
                return (Console.WindowWidth, Console.WindowHeight);
            }
        };





        //internal static IConsole _CreateFloatingWindow(int? x, int? y, int? width, int? height, ConsoleColor foreground, ConsoleColor background, bool echo = true, IConsole echoConsole = null, params K[] options)
        //{
        //    var theme = new Style(foreground, background).ToTheme();
        //}

        internal static IConsole _CreateFloatingWindow(IConsole console, WindowSettings settings)
        {
            var w = new Window(console, settings);
            w.SetWindowOffset(settings.SX, settings.SY ?? 0);
            return w;
        }

        internal static IConsole _CreateFloatingWindow(WindowSettings settings)
        {
            return _CreateFloatingWindow(null, settings);
        }

        private StyleTheme _theme = null;

        public StyleTheme Theme
        {
            get
            {
                lock (_locker) return _theme ?? (_theme = _console.Theme);
            }
            set
            {
                lock (_locker) _theme = value;
            }
        }

        public Style Style
        {
            get
            {
                lock (_locker) return Theme.GetActive(Status);
            }
        }

        public ControlStatus Status { get; set; } = ControlStatus.Active;

        private static int GetStartHeight(int? height, int y, IConsole echoConsole)
        {
            return height ?? (echoConsole?.WindowHeight ?? y);
        }

        private static int GetStartWidth(bool echo, int? width, int x, IConsole echoConsole)
        {
            if (width != null) return width.Value;
            // if echo is false, then this is a mock console and the width is never capped

            // should_clip_child_window_to_not_exceed_parent_boundaries

            int echoWidth = echoConsole?.WindowWidth ?? x;
            int maxWidth = (echoWidth - x);
            int w = width ?? (echoConsole?.WindowWidth ?? 120);
            if (echo && w > maxWidth) w = maxWidth;
            return w;
        }

        private void init()
        {
            if (HasTitle)
            {
                new Draw(_console, Style, Drawing.MergeOrOverlap.Fast).Box(_x - 1, _y - 1, _x + _width, _y + _height, _title);
            }

            _lastLineWrittenTo = -1;
            _lines.Clear();
            for (int i = 0; i < _height; i++)
            {
                _lines.Add(i, new Row(_width, ' ', ForegroundColor, BackgroundColor));
                if (!_transparent) _printAt(0, i, new string(' ', _width));
            }
            Cursor = new XY(0, 0);
            _lastLineWrittenTo = -1;
        }

        /// <summary>
        /// use this method to return an 'approve-able' text buffer representing the background color of the buffer
        /// </summary>
        /// <param name="highliteColor">the background color to look for that indicates that text has been hilighted</param>
        /// <param name="hiChar">the char to use to indicate a highlight</param>
        /// <param name="normal">the chart to use for all other</param>
        /// <returns></returns>
        public string[] BufferHighlighted(ConsoleColor highliteColor, char hiChar = '#', char normal = ' ')
        {
            lock (_locker)
            {
                var buffer = new HiliteBuffer(highliteColor, hiChar, normal);
                var rows = _lines.Select(l => l.Value).ToArray();
                var texts = buffer.ToApprovableText(rows);
                return texts;
            }
        }

        public string BufferHighlightedString(ConsoleColor highliteColor, char hiChar = '#', char normal = ' ')
        {
            lock (_locker)
            {
                var buffer = new HiliteBuffer(highliteColor, hiChar, normal);
                var rows = _lines.Select(l => l.Value).ToArray();
                var text = buffer.ToApprovableString(rows);
                return text;
            }
        }

        /// <summary>
        /// returns the buffer with additional 2 characters representing the background color and foreground color
        /// colors rendered using the `ColorMapper.cs`
        /// </summary>
        /// <returns></returns>
        public string[] BufferWithColor
        {
            get
            {
                lock (_locker)
                {
                    var buffer = _lines.Select(l => l.Value.ToStringWithColorChars());
                    return buffer.ToArray();
                }
            }
        }

        private string ColorString(Row row)
        {
            var chars = row.Cells.SelectMany(r => r.Value.ToChars()).ToArray();
            return new string(chars);
        }


        /// <summary>
        /// get the entire buffer (all the lines for the whole console) regardless of whether they have been written to or not, untrimmed.
        /// </summary>
        public string[] Buffer
        {
            get
            {
                lock(_locker)
                    return _lines.Values.Take(_height).Select(b => b.ToString()).ToArray();
            }
    }
        /// <summary>
        /// get the entire buffer (all the lines for the whole console) regardless of whether they have been written to or not, untrimmed. as a single `crln` concatenated string.
        /// </summary>
        public string BufferString
        {
            get
            {
                lock(_locker)
                    return string.Join("\r\n", Buffer);
            }
        }

        /// <summary>
        /// get all the lines written to for the whole console, untrimmed
        /// </summary>
        public string[] BufferWritten // should be buffer written
        {
            get { lock (_locker) return _lines.Values.Take(_lastLineWrittenTo + 1).Select(b => b.ToString()).ToArray(); }
        }

        /// <summary>
        /// get all the lines written to for the whole console - bufferWrittenString
        /// </summary>
        public string BufferWrittenString
        {
            get
            {
                lock (_locker)
                    return string.Join("\r\n", BufferWritten);
            }
        }


        /// <summary>
        /// get all the lines written to for the whole console, all trimmed.
        /// </summary>
        public string[] BufferWrittenTrimmed
        {
            get
            {
                lock (_locker)
                    return
                    _lines.Values.Take(_lastLineWrittenTo + 1).Select(b => b.ToString().TrimEnd(new[] { ' ' })).ToArray();
            }
        }

        public void Clear()
        {
            Clear(null);
        }

        public void Clear(ConsoleColor? background)
        {
            lock (_locker)
            {
                if (background.HasValue) BackgroundColor = background.Value;
                init();
            }
        }

        public virtual void MoveBufferArea(int sourceLeft, int sourceTop, int sourceWidth, int sourceHeight, int targetLeft, int targetTop,
            char sourceChar, ConsoleColor sourceForeColor, ConsoleColor sourceBackColor)
        {
            lock (_locker)
            {
                if (!_echo) return;
                if (_console != null)
                    _console.MoveBufferArea(sourceLeft + AbsoluteX, sourceTop + AbsoluteY, sourceWidth, sourceHeight, targetLeft + AbsoluteX, targetTop + AbsoluteY, sourceChar, sourceForeColor, sourceBackColor);

                else
                {
                    throw new Exception("Should never get here, something gone wrong in the logic, possibly in the constructor checks?");
                }
            }

        }

        // scroll the screen up 1 line, and pop the top line off the buffer
        //NB!Need to test if this is cross platform ?
        public void ScrollDown()
        {
            lock (_locker)
            {
                for (int i = 0; i < (_height - 1); i++)
                {
                    _lines[i] = _lines[i + 1];
                }
                _lines[_height - 1] = new Row(_width, ' ', ForegroundColor, BackgroundColor);
                Cursor = new XY(0, _height - 1);
                if (_console != null)
                {
                    _console.MoveBufferArea(_x, _y + 1, _width, _height - 1, _x, _y, ' ', ForegroundColor, BackgroundColor);
                }
            }
        }

        public int WindowHeight
        {
            get
            {
                return _height;
            }
        }

        public int CursorTop
        {
            get { lock (_locker) return Cursor.Y; }
            set { lock (_locker) Cursor = Cursor.WithY(value); }
        }

        public int CursorLeft
        {
            get { lock (_locker) return Cursor.X; }
            set { lock (_locker) Cursor = Cursor.WithX(value); }
        }

        public Colors Colors
        {
            get
            {
                lock (_locker) return new Colors(ForegroundColor, BackgroundColor);
            }
            set
            {
                lock (_locker)
                {
                    ForegroundColor = value.Foreground;
                    BackgroundColor = value.Background;
                }
            }
        }

        internal void SetWindowOffset(int x, int y)
        {
            _absoluteX = x;
            _absoluteY = y;
        }

        public int AbsoluteY => _absoluteY;
        public int AbsoluteX => _absoluteX;
        public int WindowWidth => _width;

        public ConsoleColor BackgroundColor { get; set; }

        private bool _noEchoCursorVisible = true;

        public bool CursorVisible
        {
            get { lock (_locker) return _console?.CursorVisible ?? _noEchoCursorVisible; }
            set
            {
                lock (_locker)
                {
                    if (_console == null)
                        _noEchoCursorVisible = value;
                    else
                        _console.CursorVisible = value;
                }
            }
        }



        public ConsoleColor ForegroundColor { get; set; }

        public ConsoleState State
        {
            get
            {
                lock (_locker) return new ConsoleState(ForegroundColor, BackgroundColor, CursorTop, CursorLeft, CursorVisible);
            }

            set
            {
                lock (_locker)
                {
                    CursorLeft = value.Left;
                    CursorTop = value.Top;
                    ForegroundColor = value.ForegroundColor;
                    BackgroundColor = value.BackgroundColor;
                }
            }
        }

        public void PrintAt(int x, int y, string format, params object[] args)
        {
            DoCommand(this, () =>
            {
                var text = string.Format(format, args);
                Cursor = new XY(x, y);
                Write(text);
            });
        }

        private void _printAt(int x, int y, string text)
        {
            Cursor = new XY(x, y);
            Write(text);
        }

        public void PrintAt(int x, int y, string text)
        {
            DoCommand(this, () =>
            {
                Cursor = new XY(x, y);
                Write(text);
            });
        }

        public void PrintAt(int x, int y, char c)
        {
            DoCommand(this, () =>
            {
                Cursor = new XY(x, y);
                Write(c.ToString());
            });
        }

        public void PrintAt(Colors colors, int x, int y, string format, params object[] args)
        {
            DoCommand(this, () =>
            {
                Cursor = new XY(x, y);
                Colors = colors;
                var text = string.Format(format, args);
                Write(text);
            });
        }

        public void PrintAt(ConsoleColor color, int x, int y, string format, params object[] args)
        {
            DoCommand(this, () =>
            {
                Cursor = new XY(x, y);
                ForegroundColor = color;
                var text = string.Format(format, args);
                Write(text);
            });
        }

        public void PrintAt(Colors colors, int x, int y, string text)
        {
            DoCommand(this, () =>
            {
                Cursor = new XY(x, y);
                Colors = colors;
                Write(text);
            });
        }

        public void PrintAt(ConsoleColor color, int x, int y, string text)
        {
            DoCommand(this, () =>
            {
                Cursor = new XY(x, y);
                ForegroundColor = color;
                Write(text);
            });
        }



        public void PrintAt(Colors colors, int x, int y, char c)
        {
            DoCommand(this, () =>
            {
                Colors = colors;
                PrintAt(x, y, c);
            });
        }

        public void PrintAt(ConsoleColor color, int x, int y, char c)
        {
            DoCommand(this, () =>
            {
                ForegroundColor = color;
                PrintAt(x, y, c);
            });
        }

        /// <summary>
        /// Run command and preserve the state, i.e. restore the console state after running command.
        /// </summary>
        public void DoCommand(IConsole console, Action action)
        {
            lock(_locker)
            {
                if (console == null)
                {
                    action();
                    return;
                }
                var state = console.State;
                try
                {
                    GotoEchoCursor(console);
                    action();
                }
                finally
                {
                    console.State = state;
                }
            }
        }

        private void GotoEchoCursor(IConsole console)
        {
            console.CursorTop = _cursor.Y + _y;
            console.CursorLeft = (_cursor.X + _x);
        }

        public void Fill(ConsoleColor color, int sx, int sy, int width, int height)
        {
            DoCommand(this, () =>
            {
                ForegroundColor = color;
                var line = new String(' ', width);
                for (int y = sy; y < height; y++)
                {
                    PrintAt(sx, y, line);
                }
            });
        }

        public Cell Peek(int sx, int sy)
        {
            lock(_locker)
            {
                if (sx > WindowWidth || sy > WindowHeight) return Cell.Default;
                return _lines[sy].Cells[sx].Clone();
            }
        }

        public Row[] Peek(ConsoleRegion region)
        {
            lock (_locker)
            {
                int height = region.EndY - region.StartY;
                if (height < 1 || region.StartY > WindowHeight) return new[] { new Row() };
                int width = region.EndX - region.StartX;
                var rows = Enumerable.Range(region.StartY, region.EndY)
                    .Select(y => Peek(region.StartX, region.StartY, width))
                    .ToArray();
                return rows;
            }
        }

        public Row Peek(int sx, int sy, int width)
        {
            lock (_locker)
            {
                int len = sx + width > WindowWidth ? width - sx : width;
                if (width < 1 || len < 1 || sx > WindowWidth) return new Row();
                // perfect canidate for span, but only if cells were immutable, which they are not!
                var cells = _lines[sy].Cells.Skip(sx).Take(len).Select(c => c.Value.Clone()).ToArray();
                var row = new Row(cells);
                return row;
            }
        }
    }
}
