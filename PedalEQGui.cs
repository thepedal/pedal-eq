// PedalEQGui — read-only band summary panel for Pedal EQ
// Discovered by ReBuzz via IMachineGUIFactory assembly scan (Core §26).

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace WDE.PedalEQ
{
    [MachineGUIFactoryDecl(PreferWindowedGUI = false)]
    public class PedalEQGuiFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new PedalEQGui();
    }

    public class PedalEQGui : UserControl, IMachineGUI
    {
        static readonly Brush BG        = Freeze(new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x1c)));
        static readonly Brush FG        = Freeze(new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)));
        static readonly Brush FG_DIM    = Freeze(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)));
        static readonly Brush FG_FLAT   = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
        static readonly Brush COL_BOOST = Freeze(new SolidColorBrush(Color.FromRgb(0x55, 0xaa, 0xff)));
        static readonly Brush COL_CUT   = Freeze(new SolidColorBrush(Color.FromRgb(0xff, 0x88, 0x33)));
        static readonly Brush COL_SOLO  = Freeze(new SolidColorBrush(Color.FromRgb(0xff, 0xcc, 0x00)));
        static readonly Brush COL_BYP   = Freeze(new SolidColorBrush(Color.FromRgb(0xff, 0x44, 0x44)));
        static readonly Brush SEP_BG    = Freeze(new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38)));
        static readonly FontFamily MONO = new FontFamily("Consolas");
        static T Freeze<T>(T b) where T : Freezable { b.Freeze(); return b; }

        PedalEQMachine _eq;
        IMachine _machine;
        public IMachine Machine
        {
            get => _machine;
            set { _machine = value; _eq = value?.ManagedMachine as PedalEQMachine; }
        }

        TextBlock _tbBypass;
        readonly TextBlock[] _tbFreq = new TextBlock[4];
        readonly TextBlock[] _tbGain = new TextBlock[4];
        readonly TextBlock[] _tbQ    = new TextBlock[4];
        readonly TextBlock[] _tbSolo = new TextBlock[4];
        TextBlock _tbOut;
        DispatcherTimer _timer;

        public PedalEQGui()
        {
            Background = BG;
            Padding    = new Thickness(10, 8, 10, 8);
            var root   = new StackPanel();
            Content    = root;

            var header = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = Lbl("PEDAL EQ", FG, 10.5, FontWeights.Bold);
            Grid.SetColumn(title, 0);
            _tbBypass = Lbl("", COL_BYP, 9, FontWeights.Bold);
            _tbBypass.HorizontalAlignment = HorizontalAlignment.Right;
            _tbBypass.VerticalAlignment   = VerticalAlignment.Center;
            Grid.SetColumn(_tbBypass, 1);
            header.Children.Add(title);
            header.Children.Add(_tbBypass);
            root.Children.Add(header);
            root.Children.Add(Sep());

            string[] names = { "LS", "LM", "HM", "HS" };
            bool[]   hasQ  = { false, true, true, false };
            for (int i = 0; i < 4; i++)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var lbl = Lbl(names[i], FG_DIM, 9.5, FontWeights.Bold);
                Grid.SetColumn(lbl, 0);
                _tbFreq[i] = Lbl("---", FG_FLAT, 9.5); Grid.SetColumn(_tbFreq[i], 1);
                _tbGain[i] = Lbl("---", FG_FLAT, 9.5); Grid.SetColumn(_tbGain[i], 2);
                _tbQ[i]    = Lbl(hasQ[i] ? "---" : "", FG_DIM, 9.5); Grid.SetColumn(_tbQ[i], 3);
                _tbSolo[i] = Lbl("", COL_SOLO, 8.5, FontWeights.Bold);
                _tbSolo[i].HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(_tbSolo[i], 4);
                row.Children.Add(lbl); row.Children.Add(_tbFreq[i]);
                row.Children.Add(_tbGain[i]); row.Children.Add(_tbQ[i]);
                row.Children.Add(_tbSolo[i]);
                root.Children.Add(row);
            }

            root.Children.Add(Sep());
            var outRow = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            outRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            outRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var outLbl = Lbl("Out", FG_DIM, 9.5, FontWeights.Bold); Grid.SetColumn(outLbl, 0);
            _tbOut = Lbl("---", FG_FLAT, 9.5); Grid.SetColumn(_tbOut, 1);
            outRow.Children.Add(outLbl); outRow.Children.Add(_tbOut);
            root.Children.Add(outRow);

            Width  = 248;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += (_, __) => Refresh();
            _timer.Start();
            Unloaded += (_, __) => _timer.Stop();
        }

        static TextBlock Lbl(string t, Brush fg, double sz, FontWeight? w = null) =>
            new TextBlock { Text = t, Foreground = fg, FontFamily = MONO,
                            FontSize = sz, FontWeight = w ?? FontWeights.Normal,
                            VerticalAlignment = VerticalAlignment.Center };

        static Separator Sep() => new Separator
            { Margin = new Thickness(0, 4, 0, 4), Background = SEP_BG, Height = 1 };

        static readonly int[][] FREQ_TABLES =
        {
            PedalEQMachine.LS_FREQS, PedalEQMachine.LM_FREQS,
            PedalEQMachine.HM_FREQS, PedalEQMachine.HS_FREQS,
        };

        void Refresh()
        {
            if (_eq == null) return;

            _tbBypass.Text = _eq.Bypass != 0 ? "BYPASS" : "";

            int[] freqIdx = { _eq.LSFreq, _eq.LMFreq, _eq.HMFreq, _eq.HSFreq };
            int[] gainRaw = { _eq.LSGain, _eq.LMGain, _eq.HMGain, _eq.HSGain };
            int[] soloOn  = { _eq.LSSolo, _eq.LMSolo, _eq.HMSolo, _eq.HSSolo };
            int[] qIdx    = { 0,          _eq.LMQ,    _eq.HMQ,    0          };
            bool[] hasQ   = { false,      true,       true,       false       };

            for (int i = 0; i < 4; i++)
            {
                var table = FREQ_TABLES[i];
                int hz    = table[Math.Clamp(freqIdx[i], 0, table.Length - 1)];
                _tbFreq[i].Text = hz >= 1000
                    ? $"{hz / 1000.0,4:0.##} kHz"
                    : $"{hz,4} Hz ";

                int    gr = gainRaw[i];
                double db = (gr - 48) * 0.5;
                _tbGain[i].Text       = gr == 48 ? " 0.0 dB" : $"{db:+0.0;-0.0} dB";
                _tbGain[i].Foreground = gr > 48 ? COL_BOOST : gr < 48 ? COL_CUT : FG_FLAT;

                if (hasQ[i])
                {
                    double q = PedalEQMachine.Q_VALUES[Math.Clamp(qIdx[i], 0, PedalEQMachine.Q_VALUES.Length - 1)];
                    _tbQ[i].Text = q < 10 ? $"Q {q:0.0}" : $"Q{q:0.0}";
                }

                _tbSolo[i].Text = soloOn[i] != 0 ? "SOLO" : "";
            }

            int    or2 = _eq.OutGain;
            double odb = (or2 - 48) * 0.5;
            _tbOut.Text       = or2 == 48 ? " 0.0 dB" : $"{odb:+0.0;-0.0} dB";
            _tbOut.Foreground = or2 > 48 ? COL_BOOST : or2 < 48 ? COL_CUT : FG_FLAT;
        }
    }
}
