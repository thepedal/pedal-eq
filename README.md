# Pedal EQ

A clean, precise **4-band parametric EQ** effect machine for
[ReBuzz](https://github.com/wasteddesign/ReBuzz), built on biquad
(Direct Form I) filters using the standard Audio EQ Cookbook formulae.

---

## Bands

| # | Type       | Frequency range | Default freq | Gain range |
|---|------------|----------------|--------------|------------|
| 1 | Low Shelf  | 20 – 500 Hz    | 80 Hz        | ±24 dB     |
| 2 | Low-Mid Peak | 100 – 5 000 Hz | 400 Hz     | ±24 dB     |
| 3 | High-Mid Peak | 500 – 16 000 Hz | 3 000 Hz | ±24 dB     |
| 4 | High Shelf | 1 000 – 20 000 Hz | 8 000 Hz  | ±24 dB     |
|   | Output Gain | —             | —            | ±24 dB     |

---

## Parameters

| Parameter | Range        | Default | Notes                                      |
|-----------|--------------|---------|--------------------------------------------|
| LS Freq   | 20 – 500 Hz  | 80      | Low shelf corner frequency                 |
| LS Gain   | −24 – +24 dB | 0       | Low shelf level                            |
| LM Freq   | 100 – 5000 Hz| 400     | Low-mid bell centre frequency              |
| LM Gain   | −24 – +24 dB | 0       | Low-mid boost/cut                          |
| LM Q×10   | 1 – 200      | 14      | Low-mid Q (÷10) — 14 → Q 1.4              |
| HM Freq   | 500 – 16000 Hz| 3000   | High-mid bell centre frequency             |
| HM Gain   | −24 – +24 dB | 0       | High-mid boost/cut                         |
| HM Q×10   | 1 – 200      | 14      | High-mid Q (÷10)                           |
| HS Freq   | 1000 – 20000 Hz | 8000 | High shelf corner frequency               |
| HS Gain   | −24 – +24 dB | 0       | High shelf level                           |
| Output    | −24 – +24 dB | 0       | Post-EQ output trim                        |

**Q guide**: Q 0.7 is broad (musical), Q 1.4 is medium, Q 4+ is surgical.  
Parameter value ÷ 10 = Q — so `LM Q×10 = 14` means Q = 1.4.

---

## Requirements

- [ReBuzz](https://github.com/wasteddesign/ReBuzz) (1812-preview or later)
- [.NET 10 Desktop Runtime – Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0)
- [.NET 10 SDK – Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0) — build only

---

## Installation (build from source)

### Quick build (Windows)

1. Install the **.NET 10 SDK** from the link above.
2. Double-click **`build.bat`** (or run it from a terminal).
3. Restart ReBuzz — **Pedal EQ** appears under Effects.

> If ReBuzz is installed outside `C:\Program Files\ReBuzz`, open
> `build.bat` in Notepad and edit the `REBUZZ_DIR` line at the top.

### Manual build

```powershell
dotnet build PedalEQ.csproj -c Release
```

Override the install path if needed:

```powershell
dotnet build PedalEQ.csproj -c Release /p:BuzzDir="D:\MyReBuzz"
```

The DLL is written directly to `<BuzzDir>\Gear\Effects\Pedal EQ.NET.dll`.

---

## DSP design notes

Each band is a single second-order IIR biquad (Direct Form I), using
the Audio EQ Cookbook (R. Bristow-Johnson) coefficient formulae:

- **Peaking/bell** — boost or cut around a centre frequency.
- **Low/High shelf** — slope S = 1 (Butterworth-matched) with an
  `alpha = sin(w0) / √2` term, giving a smooth 6 dB/oct shelf.

Coefficients are recomputed **only when a parameter or the host sample
rate changes**, keeping the audio thread lean. When all gains are 0 dB
(flat), a zero-overhead block copy replaces the filter loop entirely.

Direct Form I was chosen over Transposed Form II because it has better
numerical behaviour at high Q and low frequencies near Nyquist, at the
cost of two extra state variables per band — negligible for 4 bands.

---

## License

MIT
