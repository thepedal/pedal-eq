// Pedal EQ – ReBuzz managed effect machine  v1.2
//
// Improvements over v1.0:
//   • Gain resolution: 0.5 dB steps (±24 dB), displayed as formatted dB strings
//   • Logarithmic frequency tables (ISO 1/3-octave series) with kHz display
//   • Bypass parameter for instant A/B comparison
//   • Coefficient smoothing: 256-sample crossfade eliminates zipper noise
//   • Output gain ramp: per-sample linear ramp prevents clicks on gain changes
//
// v1.2 additions:
//   • Per-band Solo (LS Solo / LM Solo / HM Solo / HS Solo)
//     Engage one or more solos to hear only those bands in isolation.
//     Solo routes through the existing crossfade — no additional DSP cost.
//
// DSP: Audio EQ Cookbook (R. Bristow-Johnson) biquad formulae, Direct Form I.
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
        public float b0, b1, b2;    // feed-forward
        public float a1, a2;        // feed-back (a0 normalised away)

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

        // ── Peaking (bell) EQ ─────────────────────────────────────────────────
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

        // ── Low shelf (S = 1, Butterworth-matched) ────────────────────────────
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

        // ── High shelf (S = 1, Butterworth-matched) ───────────────────────────
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
        // Parameter value = array index.

        static readonly int[] LS_FREQS =
        //   idx:  0   1   2   3   4   5    6    7    8    9   10   11   12   13   14
            { 20, 25, 32, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500 };

        static readonly int[] LM_FREQS =
        //   idx:   0    1    2    3    4    5    6    7    8    9    10    11    12    13    14    15    16    17
            { 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000 };

        static readonly int[] HM_FREQS =
        //   idx:   0    1    2     3     4     5     6     7     8     9     10    11    12    13     14    15
            { 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000 };

        static readonly int[] HS_FREQS =
        //   idx:     0     1     2     3     4     5     6     7     8     9    10     11     12     13
            { 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000 };

        static readonly double[] Q_VALUES =
        //   idx:   0     1     2     3     4     5     6     7     8     9    10    11    12    13    14    15    16
            { 0.3, 0.4,  0.5,  0.6,  0.7,  0.8,  1.0,  1.2,  1.4,  1.7,  2.0,  2.5,  3.0,  4.0,  5.0,  7.0, 10.0 };

        // ── DSP state ─────────────────────────────────────────────────────────
        readonly BiquadState[]  stL   = new BiquadState[4];
        readonly BiquadState[]  stR   = new BiquadState[4];
        readonly BiquadCoeffs[] c     = new BiquadCoeffs[4]; // current target
        readonly BiquadCoeffs[] cPrev = new BiquadCoeffs[4]; // previous (blend from)

        // 256-sample coefficient crossfade eliminates zipper noise on any
        // parameter change.  At 48 kHz this is ~5 ms — inaudible as a sweep,
        // but long enough to completely suppress the discontinuity artefact.
        const int SMOOTH_SAMPLES = 256;
        int _blendRemain = 0;

        // Per-sample output gain ramp prevents clicks when Output is adjusted.
        float _gainCurrent = 1f;

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
        //  Gain   MinValue = -48, MaxValue = 48  →  actual dB = paramValue × 0.5
        //         0 = 0.0 dB (flat), 97 ValueDescriptions strings
        //
        //  Freq   MinValue = 0, MaxValue = table.Length − 1  (index into table)
        //
        //  Q      MinValue = 0, MaxValue = 16  (index into Q_VALUES)

        // ── Bypass ────────────────────────────────────────────────────────────

        [ParameterDecl(
            Name              = "Bypass",
            Description       = "Bypass all EQ processing (A/B comparison)",
            ValueDescriptions = new[] { "Off", "On" },
            DefValue          = 0)]
        public int Bypass { get; set; } = 0;

        // ── Per-band Solo ─────────────────────────────────────────────────────
        // When any solo is active, only soloed bands apply their filter;
        // all others are replaced with an identity (flat) biquad.
        // Multiple solos can be active simultaneously.

        [ParameterDecl(
            Name              = "LS Solo",
            Description       = "Solo the Low Shelf band — all other bands bypassed",
            MinValue          = 0,
            MaxValue          = 1,
            ValueDescriptions = new[] { "Off", "On" },
            DefValue          = 0)]
        public int LSSolo { get; set; } = 0;

        [ParameterDecl(
            Name              = "LM Solo",
            Description       = "Solo the Low-Mid band — all other bands bypassed",
            MinValue          = 0,
            MaxValue          = 1,
            ValueDescriptions = new[] { "Off", "On" },
            DefValue          = 0)]
        public int LMSolo { get; set; } = 0;

        [ParameterDecl(
            Name              = "HM Solo",
            Description       = "Solo the High-Mid band — all other bands bypassed",
            MinValue          = 0,
            MaxValue          = 1,
            ValueDescriptions = new[] { "Off", "On" },
            DefValue          = 0)]
        public int HMSolo { get; set; } = 0;

        [ParameterDecl(
            Name              = "HS Solo",
            Description       = "Solo the High Shelf band — all other bands bypassed",
            MinValue          = 0,
            MaxValue          = 1,
            ValueDescriptions = new[] { "Off", "On" },
            DefValue          = 0)]
        public int HSSolo { get; set; } = 0;

        // ── Band 1 – Low Shelf ────────────────────────────────────────────────

        [ParameterDecl(
            Name              = "LS Freq",
            Description       = "Low shelf corner frequency",
            MinValue          = 0,
            MaxValue          = 14,
            DefValue          = 6,   // 80 Hz
            ValueDescriptions = new[]
            {
                "20 Hz", "25 Hz", "32 Hz", "40 Hz", "50 Hz", "63 Hz", "80 Hz",
                "100 Hz", "125 Hz", "160 Hz", "200 Hz", "250 Hz", "315 Hz", "400 Hz", "500 Hz"
            })]
        public int LSFreq { get; set; } = 6;

        [ParameterDecl(
            Name              = "LS Gain",
            Description       = "Low shelf gain (0.5 dB steps, 0 = flat)",
            MinValue          = -48,
            MaxValue          = 48,
            DefValue          = 0,
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
        public int LSGain { get; set; } = 0;

        // ── Band 2 – Low-Mid Peak ─────────────────────────────────────────────

        [ParameterDecl(
            Name              = "LM Freq",
            Description       = "Low-mid bell centre frequency",
            MinValue          = 0,
            MaxValue          = 17,
            DefValue          = 6,   // 400 Hz
            ValueDescriptions = new[]
            {
                "100 Hz", "125 Hz", "160 Hz", "200 Hz", "250 Hz", "315 Hz", "400 Hz", "500 Hz",
                "630 Hz", "800 Hz", "1.0 kHz", "1.25 kHz", "1.6 kHz", "2.0 kHz", "2.5 kHz",
                "3.15 kHz", "4.0 kHz", "5.0 kHz"
            })]
        public int LMFreq { get; set; } = 6;

        [ParameterDecl(
            Name              = "LM Gain",
            Description       = "Low-mid peak gain (0.5 dB steps, 0 = flat)",
            MinValue          = -48,
            MaxValue          = 48,
            DefValue          = 0,
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
        public int LMGain { get; set; } = 0;

        [ParameterDecl(
            Name              = "LM Q",
            Description       = "Low-mid bandwidth — lower Q = broader, higher Q = narrower",
            MinValue          = 0,
            MaxValue          = 16,
            DefValue          = 6,   // Q 1.0
            IsStateless       = true,
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
            MinValue          = 0,
            MaxValue          = 15,
            DefValue          = 8,   // 3.15 kHz
            ValueDescriptions = new[]
            {
                "500 Hz", "630 Hz", "800 Hz", "1.0 kHz", "1.25 kHz", "1.6 kHz",
                "2.0 kHz", "2.5 kHz", "3.15 kHz", "4.0 kHz", "5.0 kHz", "6.3 kHz",
                "8.0 kHz", "10 kHz", "12.5 kHz", "16 kHz"
            })]
        public int HMFreq { get; set; } = 8;

        [ParameterDecl(
            Name              = "HM Gain",
            Description       = "High-mid peak gain (0.5 dB steps, 0 = flat)",
            MinValue          = -48,
            MaxValue          = 48,
            DefValue          = 0,
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
        public int HMGain { get; set; } = 0;

        [ParameterDecl(
            Name              = "HM Q",
            Description       = "High-mid bandwidth — lower Q = broader, higher Q = narrower",
            MinValue          = 0,
            MaxValue          = 16,
            DefValue          = 6,   // Q 1.0
            IsStateless       = true,
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
            MinValue          = 0,
            MaxValue          = 13,
            DefValue          = 9,   // 8.0 kHz
            ValueDescriptions = new[]
            {
                "1.0 kHz", "1.25 kHz", "1.6 kHz", "2.0 kHz", "2.5 kHz", "3.15 kHz",
                "4.0 kHz", "5.0 kHz", "6.3 kHz", "8.0 kHz", "10 kHz", "12.5 kHz",
                "16 kHz", "20 kHz"
            })]
        public int HSFreq { get; set; } = 9;

        [ParameterDecl(
            Name              = "HS Gain",
            Description       = "High shelf gain (0.5 dB steps, 0 = flat)",
            MinValue          = -48,
            MaxValue          = 48,
            DefValue          = 0,
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
        public int HSGain { get; set; } = 0;

        // ── Output trim ───────────────────────────────────────────────────────

        [ParameterDecl(
            Name              = "Output",
            Description       = "Post-EQ output trim — ramped per sample to prevent clicks",
            MinValue          = -48,
            MaxValue          = 48,
            DefValue          = 0,
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
        public int OutGain { get; set; } = 0;

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
            // Capture the current targets as the crossfade start point.
            for (int i = 0; i < 4; i++) cPrev[i] = c[i];

            double nyq = sr * 0.499;

            // Solo logic: if any band is soloed, non-soloed bands become identity
            // (flat pass-through). Multiple solos are additive — all soloed bands
            // are heard together, which is useful for comparing adjacent bands.
            bool anySolo = LSSolo != 0 || LMSolo != 0 || HMSolo != 0 || HSSolo != 0;

            // Gain params are half-dB integers → actual dB = value × 0.5
            c[0] = (!anySolo || LSSolo != 0)
                ? BiquadCoeffs.LowShelf(
                      Math.Min(LS_FREQS[LSFreq], nyq),
                      LSGain * 0.5,
                      sr)
                : BiquadCoeffs.Identity();

            c[1] = (!anySolo || LMSolo != 0)
                ? BiquadCoeffs.Peak(
                      Math.Min(LM_FREQS[LMFreq], nyq),
                      LMGain * 0.5,
                      Q_VALUES[LMQ],
                      sr)
                : BiquadCoeffs.Identity();

            c[2] = (!anySolo || HMSolo != 0)
                ? BiquadCoeffs.Peak(
                      Math.Min(HM_FREQS[HMFreq], nyq),
                      HMGain * 0.5,
                      Q_VALUES[HMQ],
                      sr)
                : BiquadCoeffs.Identity();

            c[3] = (!anySolo || HSSolo != 0)
                ? BiquadCoeffs.HighShelf(
                      Math.Min(HS_FREQS[HSFreq], nyq),
                      HSGain * 0.5,
                      sr)
                : BiquadCoeffs.Identity();

            // Arm the crossfade. _blendRemain counts down from SMOOTH_SAMPLES
            // to 0 inside Work(); alpha = 1 − remain/SMOOTH_SAMPLES sweeps 0→1.
            _blendRemain = SMOOTH_SAMPLES;
        }

        // =========================================================================
        //  Audio work
        // =========================================================================

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (mode == WorkModes.WM_NOIO)
                return false;

            // ── Bypass: transparent copy, no processing ───────────────────────
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

            // ── Output gain ramp ──────────────────────────────────────────────
            // OutGain × 0.5 = dB  →  linear = 10^(dB/20) = 10^(OutGain/40)
            // Expressed compactly as 10^(OutGain × 0.025).
            float targetGain = MathF.Pow(10f, OutGain * 0.025f);
            float gainStep   = (targetGain - _gainCurrent) / n;

            // ── Zero-work fast path ───────────────────────────────────────────
            // Skip DSP entirely when the EQ is provably flat and settled.
            // A solo being active means bands are suppressed, so we can't skip.
            bool anySolo = LSSolo != 0 || LMSolo != 0 || HMSolo != 0 || HSSolo != 0;
            bool allFlat = !anySolo
                        && _blendRemain == 0
                        && LSGain  == 0 && LMGain == 0
                        && HMGain  == 0 && HSGain == 0
                        && OutGain == 0
                        && MathF.Abs(_gainCurrent - 1f) < 1e-6f;
            if (allFlat)
            {
                Array.Copy(input, output, n);
                return true;
            }

            // ── Main DSP loop ─────────────────────────────────────────────────
            for (int i = 0; i < n; i++)
            {
                // Coefficient blend: smoothly crossfades old→new coefficients
                // over SMOOTH_SAMPLES, silencing zipper noise on any param change.
                BiquadCoeffs b0, b1, b2, b3;
                if (_blendRemain > 0)
                {
                    // alpha = 0 at blend start (fully old), 1 at blend end (fully new).
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

                float l = input[i].L;
                float r = input[i].R;

                // Four bands in series — Low Shelf → Low-Mid → High-Mid → High Shelf
                l = stL[0].Process(l, b0);  r = stR[0].Process(r, b0);
                l = stL[1].Process(l, b1);  r = stR[1].Process(r, b1);
                l = stL[2].Process(l, b2);  r = stR[2].Process(r, b2);
                l = stL[3].Process(l, b3);  r = stR[3].Process(r, b3);

                // Per-sample gain ramp
                _gainCurrent += gainStep;
                output[i] = new Sample(l * _gainCurrent, r * _gainCurrent);
            }

            // Snap to exact target to prevent float accumulation drift.
            _gainCurrent = targetGain;

            return true;
        }
    }
}
