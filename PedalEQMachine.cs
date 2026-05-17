// Pedal EQ – ReBuzz managed effect machine  v1.2
//
// 4-band parametric EQ: Low Shelf | Low-Mid Peak | High-Mid Peak | High Shelf
//
// DSP: Audio EQ Cookbook (R. Bristow-Johnson) biquad formulae, Direct Form I.
//
// Key design notes:
//   • Gain params: MinValue=0, MaxValue=96, DefValue=48 (flat). 0=−24dB, 48=0dB, 96=+24dB.
//     actual dB = (paramValue − 48) × 0.5  — see Pedal Comp addendum §2.
//   • Freq params: index into ISO 1/3-octave table.
//   • Solo uses parallel topology: each band filters dry input independently;
//     only soloed bands' outputs are summed. Non-soloed bands produce silence.
//   • Silence detection: after SILENCE_HOLDOFF silent buffers, flush all filter
//     states and return false. WM_NOIO is treated as immediate confirmed silence —
//     do NOT reset the holdoff counter there (would prevent sleep when upstream
//     machine returns false, e.g. a muted Pedal Tracker).
//
// Build:   dotnet build PedalEQ.csproj -c Release
// Output:  C:\Program Files\ReBuzz\Gear\Effects\Pedal EQ.NET.dll

using System;
using Buzz.MachineInterface;

namespace WDE.PedalEQ
{
    // =========================================================================
    //  Biquad coefficients — pre-normalised by a0 (Direct Form I)
    // =========================================================================

    internal struct BiquadCoeffs
    {
        public float b0, b1, b2;
        public float a1, a2;

        public static BiquadCoeffs Identity() =>
            new BiquadCoeffs { b0 = 1f };

        public static BiquadCoeffs Lerp(in BiquadCoeffs a, in BiquadCoeffs b, float t)
        {
            float u = 1f - t;
            return new BiquadCoeffs
            {
                b0 = a.b0 * u + b.b0 * t,
                b1 = a.b1 * u + b.b1 * t,
                b2 = a.b2 * u + b.b2 * t,
                a1 = a.a1 * u + b.a1 * t,
                a2 = a.a2 * u + b.a2 * t
            };
        }

        public static BiquadCoeffs Peak(double freq, double gainDb, double q, double sr)
        {
            if (Math.Abs(gainDb) < 1e-6) return Identity();
            double A     = Math.Pow(10.0, gainDb / 40.0);
            double w0    = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cw    = Math.Cos(w0);
            double a0i   = 1.0 / (1.0 + alpha / A);
            return new BiquadCoeffs
            {
                b0 = (float)((1.0 + alpha * A) * a0i),
                b1 = (float)((-2.0 * cw)       * a0i),
                b2 = (float)((1.0 - alpha * A) * a0i),
                a1 = (float)((-2.0 * cw)       * a0i),
                a2 = (float)((1.0 - alpha / A) * a0i)
            };
        }

        public static BiquadCoeffs LowShelf(double freq, double gainDb, double sr)
        {
            if (Math.Abs(gainDb) < 1e-6) return Identity();
            double A    = Math.Pow(10.0, gainDb / 40.0);
            double w0   = 2.0 * Math.PI * freq / sr;
            double cw   = Math.Cos(w0);
            double sw   = Math.Sin(w0);
            double sqA  = Math.Sqrt(A);
            double alph = sw / Math.Sqrt(2.0);
            double a0i  = 1.0 / ((A+1) + (A-1)*cw + 2*sqA*alph);
            return new BiquadCoeffs
            {
                b0 = (float)(A  * ((A+1) - (A-1)*cw + 2*sqA*alph) * a0i),
                b1 = (float)(2  *  A * ((A-1) - (A+1)*cw)         * a0i),
                b2 = (float)(A  * ((A+1) - (A-1)*cw - 2*sqA*alph) * a0i),
                a1 = (float)(-2 * ((A-1) + (A+1)*cw)               * a0i),
                a2 = (float)(((A+1) + (A-1)*cw - 2*sqA*alph)       * a0i)
            };
        }

        public static BiquadCoeffs HighShelf(double freq, double gainDb, double sr)
        {
            if (Math.Abs(gainDb) < 1e-6) return Identity();
            double A    = Math.Pow(10.0, gainDb / 40.0);
            double w0   = 2.0 * Math.PI * freq / sr;
            double cw   = Math.Cos(w0);
            double sw   = Math.Sin(w0);
            double sqA  = Math.Sqrt(A);
            double alph = sw / Math.Sqrt(2.0);
            double a0i  = 1.0 / ((A+1) - (A-1)*cw + 2*sqA*alph);
            return new BiquadCoeffs
            {
                b0 = (float)( A  * ((A+1) + (A-1)*cw + 2*sqA*alph) * a0i),
                b1 = (float)(-2  *  A * ((A-1) + (A+1)*cw)         * a0i),
                b2 = (float)( A  * ((A+1) + (A-1)*cw - 2*sqA*alph) * a0i),
                a1 = (float)( 2  * ((A-1) - (A+1)*cw)               * a0i),
                a2 = (float)(((A+1) - (A-1)*cw - 2*sqA*alph)        * a0i)
            };
        }
    }

    // =========================================================================
    //  Biquad state — Direct Form I, one channel
    // =========================================================================

    internal struct BiquadState
    {
        float x1, x2, y1, y2;

        public float Process(float x, in BiquadCoeffs c)
        {
            float y = c.b0 * x + c.b1 * x1 + c.b2 * x2
                               - c.a1 * y1  - c.a2 * y2;
            x2 = x1; x1 = x;
            y2 = y1; y1 = y;
            return y;
        }

        public void Reset() { x1 = x2 = y1 = y2 = 0f; }
    }

    // =========================================================================
    //  Machine
    // =========================================================================

    [MachineDecl(
        Name        = "Pedal EQ",
        ShortName   = "PdlEQ",
        Author      = "WDE",
        MaxTracks   = 0,
        InputCount  = 1,
        OutputCount = 1)]
    public class PedalEQMachine : IBuzzMachine
    {
        readonly IBuzzMachineHost host;

        // ── Frequency & Q lookup tables (ISO 1/3-octave series) ───────────────
        // internal: PedalEQGui reads these directly for display formatting.

        internal static readonly int[] LS_FREQS =
            { 20, 25, 32, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500 };

        internal static readonly int[] LM_FREQS =
            { 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000 };

        internal static readonly int[] HM_FREQS =
            { 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000 };

        internal static readonly int[] HS_FREQS =
            { 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000 };

        internal static readonly double[] Q_VALUES =
            { 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 1.0, 1.2, 1.4, 1.7, 2.0, 2.5, 3.0, 4.0, 5.0, 7.0, 10.0 };

        // ── DSP state — serial chain ──────────────────────────────────────────
        readonly BiquadState[]  stL   = new BiquadState[4];
        readonly BiquadState[]  stR   = new BiquadState[4];
        readonly BiquadCoeffs[] c     = new BiquadCoeffs[4];
        readonly BiquadCoeffs[] cPrev = new BiquadCoeffs[4];

        const int SMOOTH_SAMPLES = 256;
        int   _blendRemain = 0;
        float _gainCurrent = 1f;

        // ── DSP state — parallel chain (solo mode) ────────────────────────────
        // Solo switches to parallel topology so each band filters the dry input
        // independently; only soloed bands' outputs are summed.
        readonly BiquadState[] stSoloL = new BiquadState[4];
        readonly BiquadState[] stSoloR = new BiquadState[4];
        bool _prevAnySolo = false;

        // ── Silence detection ─────────────────────────────────────────────────
        // Input peak below SILENCE_THRESHOLD for SILENCE_HOLDOFF consecutive
        // buffers → flush all filter states and return false (ReBuzz contract
        // for "silent output"; skips downstream processing).
        //
        // IMPORTANT: WM_NOIO must NOT reset _silentBuffers. When an upstream
        // machine (e.g. Pedal Tracker) is muted it returns false from its own
        // Work(), causing ReBuzz to pass WM_NOIO to this machine. Resetting
        // the counter there would prevent sleep from ever accumulating.
        // WM_NOIO is treated as immediate confirmed silence instead.
        const float SILENCE_THRESHOLD = 1.0f;   // ~−90 dBFS relative to ±32768
        const int   SILENCE_HOLDOFF   = 32;     // buffers; ~170 ms at 44.1kHz/256
        int  _silentBuffers = 0;
        bool _isSleeping    = false;

        // ── Parameter cache ───────────────────────────────────────────────────
        int _sr;
        int _lsF, _lsG, _lmF, _lmG, _lmQ, _hmF, _hmG, _hmQ, _hsF, _hsG;
        int _lsSolo, _lmSolo, _hmSolo, _hsSolo;

        // =========================================================================
        //  Constructor
        // =========================================================================

        public PedalEQMachine(IBuzzMachineHost host)
        {
            this.host = host;
            for (int i = 0; i < 4; i++)
            {
                c[i]     = BiquadCoeffs.Identity();
                cPrev[i] = BiquadCoeffs.Identity();
            }
        }

        // =========================================================================
        //  Parameters
        // =========================================================================
        //
        //  Gain   MinValue=0, MaxValue=96, DefValue=48 (= 0.0 dB, the flat point)
        //         actual dB = (paramValue − 48) × 0.5
        //         See Pedal Comp addendum §2: MinValue must be ≥ 0. Negative
        //         MinValue causes ReBuzz to silently offset the stored range,
        //         producing wrong DSP results with no error surfaced.
        //
        //  Freq   MinValue=0, MaxValue=table.Length−1 (index into freq table)
        //
        //  Q      MinValue=0, MaxValue=16 (index into Q_VALUES)

        // ── Bypass ────────────────────────────────────────────────────────────

        [ParameterDecl(
            Name              = "Bypass",
            Description       = "Bypass all EQ processing (A/B comparison)",
            ValueDescriptions = new[] { "Off", "On" },
            DefValue          = 0)]
        public int Bypass { get; set; } = 0;

        // ── Per-band Solo ─────────────────────────────────────────────────────
        // Parallel topology: soloed band filters dry input; non-soloed = silence.

        [ParameterDecl(
            Name              = "LS Solo",
            Description       = "Solo the Low Shelf band",
            MinValue          = 0, MaxValue = 1, DefValue = 0,
            ValueDescriptions = new[] { "Off", "On" })]
        public int LSSolo { get; set; } = 0;

        [ParameterDecl(
            Name              = "LM Solo",
            Description       = "Solo the Low-Mid band",
            MinValue          = 0, MaxValue = 1, DefValue = 0,
            ValueDescriptions = new[] { "Off", "On" })]
        public int LMSolo { get; set; } = 0;

        [ParameterDecl(
            Name              = "HM Solo",
            Description       = "Solo the High-Mid band",
            MinValue          = 0, MaxValue = 1, DefValue = 0,
            ValueDescriptions = new[] { "Off", "On" })]
        public int HMSolo { get; set; } = 0;

        [ParameterDecl(
            Name              = "HS Solo",
            Description       = "Solo the High Shelf band",
            MinValue          = 0, MaxValue = 1, DefValue = 0,
            ValueDescriptions = new[] { "Off", "On" })]
        public int HSSolo { get; set; } = 0;

        // ── Band 1 – Low Shelf ────────────────────────────────────────────────

        [ParameterDecl(
            Name              = "LS Freq",
            Description       = "Low shelf corner frequency",
            MinValue          = 0, MaxValue = 14, DefValue = 6,
            ValueDescriptions = new[]
            {
                "20 Hz", "25 Hz", "32 Hz", "40 Hz", "50 Hz", "63 Hz", "80 Hz",
                "100 Hz", "125 Hz", "160 Hz", "200 Hz", "250 Hz", "315 Hz", "400 Hz", "500 Hz"
            })]
        public int LSFreq { get; set; } = 6;

        [ParameterDecl(
            Name              = "LS Gain",
            Description       = "Low shelf gain (0.5 dB steps, 48 = 0.0 dB flat)",
            MinValue          = 0, MaxValue = 96, DefValue = 48,
            ValueDescriptions = new[]
            {
                "-24.0 dB", "-23.5 dB", "-23.0 dB", "-22.5 dB", "-22.0 dB", "-21.5 dB", "-21.0 dB", "-20.5 dB",
                "-20.0 dB", "-19.5 dB", "-19.0 dB", "-18.5 dB", "-18.0 dB", "-17.5 dB", "-17.0 dB", "-16.5 dB",
                "-16.0 dB", "-15.5 dB", "-15.0 dB", "-14.5 dB", "-14.0 dB", "-13.5 dB", "-13.0 dB", "-12.5 dB",
                "-12.0 dB", "-11.5 dB", "-11.0 dB", "-10.5 dB", "-10.0 dB",  "-9.5 dB",  "-9.0 dB",  "-8.5 dB",
                 "-8.0 dB",  "-7.5 dB",  "-7.0 dB",  "-6.5 dB",  "-6.0 dB",  "-5.5 dB",  "-5.0 dB",  "-4.5 dB",
                 "-4.0 dB",  "-3.5 dB",  "-3.0 dB",  "-2.5 dB",  "-2.0 dB",  "-1.5 dB",  "-1.0 dB",  "-0.5 dB",
                  "0.0 dB",  "+0.5 dB",  "+1.0 dB",  "+1.5 dB",  "+2.0 dB",  "+2.5 dB",  "+3.0 dB",  "+3.5 dB",
                 "+4.0 dB",  "+4.5 dB",  "+5.0 dB",  "+5.5 dB",  "+6.0 dB",  "+6.5 dB",  "+7.0 dB",  "+7.5 dB",
                 "+8.0 dB",  "+8.5 dB",  "+9.0 dB",  "+9.5 dB", "+10.0 dB", "+10.5 dB", "+11.0 dB", "+11.5 dB",
                "+12.0 dB", "+12.5 dB", "+13.0 dB", "+13.5 dB", "+14.0 dB", "+14.5 dB", "+15.0 dB", "+15.5 dB",
                "+16.0 dB", "+16.5 dB", "+17.0 dB", "+17.5 dB", "+18.0 dB", "+18.5 dB", "+19.0 dB", "+19.5 dB",
                "+20.0 dB", "+20.5 dB", "+21.0 dB", "+21.5 dB", "+22.0 dB", "+22.5 dB", "+23.0 dB", "+23.5 dB",
                "+24.0 dB"
            })]
        public int LSGain { get; set; } = 48;

        // ── Band 2 – Low-Mid Peak ─────────────────────────────────────────────

        [ParameterDecl(
            Name              = "LM Freq",
            Description       = "Low-mid bell centre frequency",
            MinValue          = 0, MaxValue = 17, DefValue = 6,
            ValueDescriptions = new[]
            {
                "100 Hz", "125 Hz", "160 Hz", "200 Hz", "250 Hz", "315 Hz", "400 Hz", "500 Hz",
                "630 Hz", "800 Hz", "1.0 kHz", "1.25 kHz", "1.6 kHz", "2.0 kHz", "2.5 kHz",
                "3.15 kHz", "4.0 kHz", "5.0 kHz"
            })]
        public int LMFreq { get; set; } = 6;

        [ParameterDecl(
            Name              = "LM Gain",
            Description       = "Low-mid peak gain (0.5 dB steps, 48 = 0.0 dB flat)",
            MinValue          = 0, MaxValue = 96, DefValue = 48,
            ValueDescriptions = new[]
            {
                "-24.0 dB", "-23.5 dB", "-23.0 dB", "-22.5 dB", "-22.0 dB", "-21.5 dB", "-21.0 dB", "-20.5 dB",
                "-20.0 dB", "-19.5 dB", "-19.0 dB", "-18.5 dB", "-18.0 dB", "-17.5 dB", "-17.0 dB", "-16.5 dB",
                "-16.0 dB", "-15.5 dB", "-15.0 dB", "-14.5 dB", "-14.0 dB", "-13.5 dB", "-13.0 dB", "-12.5 dB",
                "-12.0 dB", "-11.5 dB", "-11.0 dB", "-10.5 dB", "-10.0 dB",  "-9.5 dB",  "-9.0 dB",  "-8.5 dB",
                 "-8.0 dB",  "-7.5 dB",  "-7.0 dB",  "-6.5 dB",  "-6.0 dB",  "-5.5 dB",  "-5.0 dB",  "-4.5 dB",
                 "-4.0 dB",  "-3.5 dB",  "-3.0 dB",  "-2.5 dB",  "-2.0 dB",  "-1.5 dB",  "-1.0 dB",  "-0.5 dB",
                  "0.0 dB",  "+0.5 dB",  "+1.0 dB",  "+1.5 dB",  "+2.0 dB",  "+2.5 dB",  "+3.0 dB",  "+3.5 dB",
                 "+4.0 dB",  "+4.5 dB",  "+5.0 dB",  "+5.5 dB",  "+6.0 dB",  "+6.5 dB",  "+7.0 dB",  "+7.5 dB",
                 "+8.0 dB",  "+8.5 dB",  "+9.0 dB",  "+9.5 dB", "+10.0 dB", "+10.5 dB", "+11.0 dB", "+11.5 dB",
                "+12.0 dB", "+12.5 dB", "+13.0 dB", "+13.5 dB", "+14.0 dB", "+14.5 dB", "+15.0 dB", "+15.5 dB",
                "+16.0 dB", "+16.5 dB", "+17.0 dB", "+17.5 dB", "+18.0 dB", "+18.5 dB", "+19.0 dB", "+19.5 dB",
                "+20.0 dB", "+20.5 dB", "+21.0 dB", "+21.5 dB", "+22.0 dB", "+22.5 dB", "+23.0 dB", "+23.5 dB",
                "+24.0 dB"
            })]
        public int LMGain { get; set; } = 48;

        [ParameterDecl(
            Name              = "LM Q",
            Description       = "Low-mid bandwidth — lower Q = broader, higher Q = narrower",
            MinValue          = 0, MaxValue = 16, DefValue = 6,
            ValueDescriptions = new[]
            {
                "0.3", "0.4", "0.5", "0.6", "0.7", "0.8",
                "1.0", "1.2", "1.4", "1.7", "2.0", "2.5",
                "3.0", "4.0", "5.0", "7.0", "10.0"
            })]
        public int LMQ { get; set; } = 6;

        // ── Band 3 – High-Mid Peak ────────────────────────────────────────────

        [ParameterDecl(
            Name              = "HM Freq",
            Description       = "High-mid bell centre frequency",
            MinValue          = 0, MaxValue = 15, DefValue = 8,
            ValueDescriptions = new[]
            {
                "500 Hz", "630 Hz", "800 Hz", "1.0 kHz", "1.25 kHz", "1.6 kHz",
                "2.0 kHz", "2.5 kHz", "3.15 kHz", "4.0 kHz", "5.0 kHz", "6.3 kHz",
                "8.0 kHz", "10 kHz", "12.5 kHz", "16 kHz"
            })]
        public int HMFreq { get; set; } = 8;

        [ParameterDecl(
            Name              = "HM Gain",
            Description       = "High-mid peak gain (0.5 dB steps, 48 = 0.0 dB flat)",
            MinValue          = 0, MaxValue = 96, DefValue = 48,
            ValueDescriptions = new[]
            {
                "-24.0 dB", "-23.5 dB", "-23.0 dB", "-22.5 dB", "-22.0 dB", "-21.5 dB", "-21.0 dB", "-20.5 dB",
                "-20.0 dB", "-19.5 dB", "-19.0 dB", "-18.5 dB", "-18.0 dB", "-17.5 dB", "-17.0 dB", "-16.5 dB",
                "-16.0 dB", "-15.5 dB", "-15.0 dB", "-14.5 dB", "-14.0 dB", "-13.5 dB", "-13.0 dB", "-12.5 dB",
                "-12.0 dB", "-11.5 dB", "-11.0 dB", "-10.5 dB", "-10.0 dB",  "-9.5 dB",  "-9.0 dB",  "-8.5 dB",
                 "-8.0 dB",  "-7.5 dB",  "-7.0 dB",  "-6.5 dB",  "-6.0 dB",  "-5.5 dB",  "-5.0 dB",  "-4.5 dB",
                 "-4.0 dB",  "-3.5 dB",  "-3.0 dB",  "-2.5 dB",  "-2.0 dB",  "-1.5 dB",  "-1.0 dB",  "-0.5 dB",
                  "0.0 dB",  "+0.5 dB",  "+1.0 dB",  "+1.5 dB",  "+2.0 dB",  "+2.5 dB",  "+3.0 dB",  "+3.5 dB",
                 "+4.0 dB",  "+4.5 dB",  "+5.0 dB",  "+5.5 dB",  "+6.0 dB",  "+6.5 dB",  "+7.0 dB",  "+7.5 dB",
                 "+8.0 dB",  "+8.5 dB",  "+9.0 dB",  "+9.5 dB", "+10.0 dB", "+10.5 dB", "+11.0 dB", "+11.5 dB",
                "+12.0 dB", "+12.5 dB", "+13.0 dB", "+13.5 dB", "+14.0 dB", "+14.5 dB", "+15.0 dB", "+15.5 dB",
                "+16.0 dB", "+16.5 dB", "+17.0 dB", "+17.5 dB", "+18.0 dB", "+18.5 dB", "+19.0 dB", "+19.5 dB",
                "+20.0 dB", "+20.5 dB", "+21.0 dB", "+21.5 dB", "+22.0 dB", "+22.5 dB", "+23.0 dB", "+23.5 dB",
                "+24.0 dB"
            })]
        public int HMGain { get; set; } = 48;

        [ParameterDecl(
            Name              = "HM Q",
            Description       = "High-mid bandwidth — lower Q = broader, higher Q = narrower",
            MinValue          = 0, MaxValue = 16, DefValue = 6,
            ValueDescriptions = new[]
            {
                "0.3", "0.4", "0.5", "0.6", "0.7", "0.8",
                "1.0", "1.2", "1.4", "1.7", "2.0", "2.5",
                "3.0", "4.0", "5.0", "7.0", "10.0"
            })]
        public int HMQ { get; set; } = 6;

        // ── Band 4 – High Shelf ───────────────────────────────────────────────

        [ParameterDecl(
            Name              = "HS Freq",
            Description       = "High shelf corner frequency",
            MinValue          = 0, MaxValue = 13, DefValue = 9,
            ValueDescriptions = new[]
            {
                "1.0 kHz", "1.25 kHz", "1.6 kHz", "2.0 kHz", "2.5 kHz", "3.15 kHz",
                "4.0 kHz", "5.0 kHz", "6.3 kHz", "8.0 kHz", "10 kHz", "12.5 kHz",
                "16 kHz", "20 kHz"
            })]
        public int HSFreq { get; set; } = 9;

        [ParameterDecl(
            Name              = "HS Gain",
            Description       = "High shelf gain (0.5 dB steps, 48 = 0.0 dB flat)",
            MinValue          = 0, MaxValue = 96, DefValue = 48,
            ValueDescriptions = new[]
            {
                "-24.0 dB", "-23.5 dB", "-23.0 dB", "-22.5 dB", "-22.0 dB", "-21.5 dB", "-21.0 dB", "-20.5 dB",
                "-20.0 dB", "-19.5 dB", "-19.0 dB", "-18.5 dB", "-18.0 dB", "-17.5 dB", "-17.0 dB", "-16.5 dB",
                "-16.0 dB", "-15.5 dB", "-15.0 dB", "-14.5 dB", "-14.0 dB", "-13.5 dB", "-13.0 dB", "-12.5 dB",
                "-12.0 dB", "-11.5 dB", "-11.0 dB", "-10.5 dB", "-10.0 dB",  "-9.5 dB",  "-9.0 dB",  "-8.5 dB",
                 "-8.0 dB",  "-7.5 dB",  "-7.0 dB",  "-6.5 dB",  "-6.0 dB",  "-5.5 dB",  "-5.0 dB",  "-4.5 dB",
                 "-4.0 dB",  "-3.5 dB",  "-3.0 dB",  "-2.5 dB",  "-2.0 dB",  "-1.5 dB",  "-1.0 dB",  "-0.5 dB",
                  "0.0 dB",  "+0.5 dB",  "+1.0 dB",  "+1.5 dB",  "+2.0 dB",  "+2.5 dB",  "+3.0 dB",  "+3.5 dB",
                 "+4.0 dB",  "+4.5 dB",  "+5.0 dB",  "+5.5 dB",  "+6.0 dB",  "+6.5 dB",  "+7.0 dB",  "+7.5 dB",
                 "+8.0 dB",  "+8.5 dB",  "+9.0 dB",  "+9.5 dB", "+10.0 dB", "+10.5 dB", "+11.0 dB", "+11.5 dB",
                "+12.0 dB", "+12.5 dB", "+13.0 dB", "+13.5 dB", "+14.0 dB", "+14.5 dB", "+15.0 dB", "+15.5 dB",
                "+16.0 dB", "+16.5 dB", "+17.0 dB", "+17.5 dB", "+18.0 dB", "+18.5 dB", "+19.0 dB", "+19.5 dB",
                "+20.0 dB", "+20.5 dB", "+21.0 dB", "+21.5 dB", "+22.0 dB", "+22.5 dB", "+23.0 dB", "+23.5 dB",
                "+24.0 dB"
            })]
        public int HSGain { get; set; } = 48;

        // ── Output trim ───────────────────────────────────────────────────────

        [ParameterDecl(
            Name              = "Output",
            Description       = "Post-EQ output trim — ramped per sample to prevent clicks",
            MinValue          = 0, MaxValue = 96, DefValue = 48,
            ValueDescriptions = new[]
            {
                "-24.0 dB", "-23.5 dB", "-23.0 dB", "-22.5 dB", "-22.0 dB", "-21.5 dB", "-21.0 dB", "-20.5 dB",
                "-20.0 dB", "-19.5 dB", "-19.0 dB", "-18.5 dB", "-18.0 dB", "-17.5 dB", "-17.0 dB", "-16.5 dB",
                "-16.0 dB", "-15.5 dB", "-15.0 dB", "-14.5 dB", "-14.0 dB", "-13.5 dB", "-13.0 dB", "-12.5 dB",
                "-12.0 dB", "-11.5 dB", "-11.0 dB", "-10.5 dB", "-10.0 dB",  "-9.5 dB",  "-9.0 dB",  "-8.5 dB",
                 "-8.0 dB",  "-7.5 dB",  "-7.0 dB",  "-6.5 dB",  "-6.0 dB",  "-5.5 dB",  "-5.0 dB",  "-4.5 dB",
                 "-4.0 dB",  "-3.5 dB",  "-3.0 dB",  "-2.5 dB",  "-2.0 dB",  "-1.5 dB",  "-1.0 dB",  "-0.5 dB",
                  "0.0 dB",  "+0.5 dB",  "+1.0 dB",  "+1.5 dB",  "+2.0 dB",  "+2.5 dB",  "+3.0 dB",  "+3.5 dB",
                 "+4.0 dB",  "+4.5 dB",  "+5.0 dB",  "+5.5 dB",  "+6.0 dB",  "+6.5 dB",  "+7.0 dB",  "+7.5 dB",
                 "+8.0 dB",  "+8.5 dB",  "+9.0 dB",  "+9.5 dB", "+10.0 dB", "+10.5 dB", "+11.0 dB", "+11.5 dB",
                "+12.0 dB", "+12.5 dB", "+13.0 dB", "+13.5 dB", "+14.0 dB", "+14.5 dB", "+15.0 dB", "+15.5 dB",
                "+16.0 dB", "+16.5 dB", "+17.0 dB", "+17.5 dB", "+18.0 dB", "+18.5 dB", "+19.0 dB", "+19.5 dB",
                "+20.0 dB", "+20.5 dB", "+21.0 dB", "+21.5 dB", "+22.0 dB", "+22.5 dB", "+23.0 dB", "+23.5 dB",
                "+24.0 dB"
            })]
        public int OutGain { get; set; } = 48;

        // =========================================================================
        //  Coefficient management
        // =========================================================================

        bool ParamsChanged(int sr) =>
            sr      != _sr   ||
            LSFreq  != _lsF  || LSGain != _lsG ||
            LMFreq  != _lmF  || LMGain != _lmG || LMQ  != _lmQ ||
            HMFreq  != _hmF  || HMGain != _hmG || HMQ  != _hmQ ||
            HSFreq  != _hsF  || HSGain != _hsG ||
            LSSolo  != _lsSolo || LMSolo != _lmSolo ||
            HMSolo  != _hmSolo || HSSolo != _hsSolo;

        void SnapshotParams(int sr)
        {
            _sr   = sr;
            _lsF  = LSFreq;  _lsG = LSGain;
            _lmF  = LMFreq;  _lmG = LMGain;  _lmQ = LMQ;
            _hmF  = HMFreq;  _hmG = HMGain;  _hmQ = HMQ;
            _hsF  = HSFreq;  _hsG = HSGain;
            _lsSolo = LSSolo;  _lmSolo = LMSolo;
            _hmSolo = HMSolo;  _hsSolo = HSSolo;
        }

        void RecalcCoefficients(int sr)
        {
            for (int i = 0; i < 4; i++) cPrev[i] = c[i];

            double nyq = sr * 0.499;

            // Solo mode is handled in Work() via parallel topology.
            // All four bands always get their real filter coefficients.
            // actual dB = (paramValue − 48) × 0.5
            c[0] = BiquadCoeffs.LowShelf(
                       Math.Min(LS_FREQS[LSFreq], nyq),
                       (LSGain - 48) * 0.5, sr);

            c[1] = BiquadCoeffs.Peak(
                       Math.Min(LM_FREQS[LMFreq], nyq),
                       (LMGain - 48) * 0.5, Q_VALUES[LMQ], sr);

            c[2] = BiquadCoeffs.Peak(
                       Math.Min(HM_FREQS[HMFreq], nyq),
                       (HMGain - 48) * 0.5, Q_VALUES[HMQ], sr);

            c[3] = BiquadCoeffs.HighShelf(
                       Math.Min(HS_FREQS[HSFreq], nyq),
                       (HSGain - 48) * 0.5, sr);

            _blendRemain = SMOOTH_SAMPLES;
        }

        // ── Helper: flush all filter states and snap gain ─────────────────────
        void FlushAllStates()
        {
            for (int i = 0; i < 4; i++)
            {
                stL[i].Reset();     stR[i].Reset();
                stSoloL[i].Reset(); stSoloR[i].Reset();
            }
            _gainCurrent = MathF.Pow(10f, (OutGain - 48) * 0.025f);
        }

        // =========================================================================
        //  Audio work
        // =========================================================================

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            // ── WM_NOIO: ReBuzz has no input for us (upstream returned false) ──
            // Treat as immediate confirmed silence — flush states and sleep.
            // Do NOT reset _silentBuffers here: resetting would prevent sleep from
            // ever accumulating when an upstream machine (e.g. muted Pedal Tracker)
            // consistently returns false and ReBuzz consistently sends WM_NOIO.
            if (mode == WorkModes.WM_NOIO)
            {
                if (!_isSleeping)
                {
                    FlushAllStates();
                    _isSleeping = true;
                }
                return false;
            }

            // ── Wake on new signal ────────────────────────────────────────────
            // If we were sleeping, _silentBuffers is already past HOLDOFF.
            // Reset it now so we track fresh silence correctly going forward.
            if (_isSleeping)
            {
                _isSleeping    = false;
                _silentBuffers = 0;
            }

            // ── Bypass ────────────────────────────────────────────────────────
            if (Bypass != 0)
            {
                Array.Copy(input, output, n);
                return true;
            }

            // ── Coefficient update ────────────────────────────────────────────
            int sr = host.MasterInfo.SamplesPerSec;
            if (sr > 0 && ParamsChanged(sr))
            {
                RecalcCoefficients(sr);
                SnapshotParams(sr);
            }

            // ── Silence detection ─────────────────────────────────────────────
            // Scan input for peak. Silence below SILENCE_THRESHOLD for
            // SILENCE_HOLDOFF buffers → flush states, sleep, return false.
            float inputPeak = 0f;
            for (int i = 0; i < n; i++)
            {
                float al = MathF.Abs(input[i].L);
                float ar = MathF.Abs(input[i].R);
                if (al > inputPeak) inputPeak = al;
                if (ar > inputPeak) inputPeak = ar;
            }

            if (inputPeak < SILENCE_THRESHOLD)
            {
                if (++_silentBuffers > SILENCE_HOLDOFF)
                {
                    FlushAllStates();
                    _isSleeping = true;
                    return false;
                }
                // Within holdoff: continue processing so filter tail renders
                // cleanly rather than being cut off abruptly.
            }
            else
            {
                _silentBuffers = 0;
            }

            // ── Output gain ramp ──────────────────────────────────────────────
            // (OutGain − 48) × 0.5 = dB → linear = 10^(dB/20)
            float targetGain = MathF.Pow(10f, (OutGain - 48) * 0.025f);
            float gainStep   = (targetGain - _gainCurrent) / n;

            // ── Zero-work fast path ───────────────────────────────────────────
            bool anySolo = LSSolo != 0 || LMSolo != 0 || HMSolo != 0 || HSSolo != 0;
            bool allFlat = !anySolo
                        && _blendRemain == 0
                        && LSGain  == 48 && LMGain == 48
                        && HMGain  == 48 && HSGain == 48
                        && OutGain == 48
                        && MathF.Abs(_gainCurrent - 1f) < 1e-6f;
            if (allFlat)
            {
                Array.Copy(input, output, n);
                return true;
            }

            // ── Solo mode transition ──────────────────────────────────────────
            if (anySolo != _prevAnySolo)
            {
                if (anySolo)
                    for (int b = 0; b < 4; b++) { stSoloL[b].Reset(); stSoloR[b].Reset(); }
                else
                    for (int b = 0; b < 4; b++) { stL[b].Reset(); stR[b].Reset(); }
                _prevAnySolo = anySolo;
            }

            // ── Main DSP loop ─────────────────────────────────────────────────
            for (int i = 0; i < n; i++)
            {
                BiquadCoeffs b0, b1, b2, b3;
                if (_blendRemain > 0)
                {
                    float alpha = 1f - (float)_blendRemain / SMOOTH_SAMPLES;
                    b0 = BiquadCoeffs.Lerp(cPrev[0], c[0], alpha);
                    b1 = BiquadCoeffs.Lerp(cPrev[1], c[1], alpha);
                    b2 = BiquadCoeffs.Lerp(cPrev[2], c[2], alpha);
                    b3 = BiquadCoeffs.Lerp(cPrev[3], c[3], alpha);
                    _blendRemain--;
                }
                else
                {
                    b0 = c[0]; b1 = c[1]; b2 = c[2]; b3 = c[3];
                }

                float inL = input[i].L;
                float inR = input[i].R;
                float outL, outR;

                if (anySolo)
                {
                    // Parallel: each band filters dry input; sum only soloed bands.
                    BiquadCoeffs[] bArr   = { b0, b1, b2, b3 };
                    int[]          soloFlags = { LSSolo, LMSolo, HMSolo, HSSolo };
                    outL = 0f; outR = 0f;
                    for (int b = 0; b < 4; b++)
                    {
                        float bL = stSoloL[b].Process(inL, bArr[b]);
                        float bR = stSoloR[b].Process(inR, bArr[b]);
                        if (soloFlags[b] != 0) { outL += bL; outR += bR; }
                    }
                }
                else
                {
                    // Serial: Low Shelf → Low-Mid → High-Mid → High Shelf
                    outL = inL; outR = inR;
                    outL = stL[0].Process(outL, b0); outR = stR[0].Process(outR, b0);
                    outL = stL[1].Process(outL, b1); outR = stR[1].Process(outR, b1);
                    outL = stL[2].Process(outL, b2); outR = stR[2].Process(outR, b2);
                    outL = stL[3].Process(outL, b3); outR = stR[3].Process(outR, b3);
                }

                _gainCurrent += gainStep;
                output[i] = new Sample(outL * _gainCurrent, outR * _gainCurrent);
            }

            _gainCurrent = targetGain;
            return true;
        }
    }
}
