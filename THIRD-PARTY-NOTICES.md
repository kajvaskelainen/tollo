# Kolmannen osapuolen ohjelmistot

Töllö käyttää seuraavia avoimen lähdekoodin komponentteja. Kunkin komponentin lisenssiehdot
koskevat kyseistä komponenttia.

| Komponentti | Versio | Lisenssi | Lähdekoodi |
|---|---|---|---|
| LibVLC (VLC media player -kirjasto) | 3.0.24 | LGPL-2.1-or-later | https://code.videolan.org/videolan/vlc |
| LibVLCSharp, LibVLCSharp.WPF | 3.10.2 | LGPL-2.1-or-later | https://code.videolan.org/videolan/LibVLCSharp |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| VirtualizingWrapPanel | 2.5.4 | MIT | https://github.com/sbaeumlisberger/VirtualizingWrapPanel |
| .NET Runtime ja WPF | 8 | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |

## LibVLC ja LibVLCSharp (LGPL)

LibVLC ja LibVLCSharp on lisensoitu GNU Lesser General Public License -lisenssillä, versio 2.1
tai uudempi: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html

- Kirjastot ovat ohjelman mukana erillisinä, muokkaamattomina DLL-tiedostoina
  (`libvlc.dll`, `libvlccore.dll`, `libvlc\plugins\`, `LibVLCSharp*.dll`).
  Ne voi korvata omalla, yhteensopivalla versiolla.
- Kirjastojen lähdekoodi on saatavilla yllä olevista osoitteista. VLC:n Windows-binäärit
  ja niiden lähdekoodit: https://download.videolan.org/pub/videolan/vlc/
- VLC:n laajennusmoduulien yksityiskohtaiset tekijänoikeustiedot: https://www.videolan.org/legal.html

## MIT-lisensoidut komponentit

Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Copyright: CommunityToolkit.Mvvm © .NET Foundation and Contributors;
VirtualizingWrapPanel © Sebastian Bäumlisberger; .NET © .NET Foundation and Contributors.
