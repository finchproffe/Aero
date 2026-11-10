# Third-party platform notice

Aero embeds web content using Microsoft Edge WebView2.

The Aero source repository and its future license do not relicense, replace, or make open source the Microsoft Edge WebView2 Runtime. Users and redistributors must follow Microsoft's applicable runtime and redistribution terms.

The interface font is Manrope (https://github.com/googlefonts/manrope), bundled under the SIL Open Font License 1.1; see `src/Aero.App/ThirdParty/Manrope/OFL.txt`.

The interface styles bundle subsets of these fonts, cut down to Latin and Cyrillic with fontTools:

- Google Sans Flex and Roboto Flex (https://github.com/google/fonts), SIL Open Font License 1.1; see `src/Aero.App/ThirdParty/GoogleSansFlex/OFL.txt` and `src/Aero.App/ThirdParty/RobotoFlex/OFL.txt`.
- Inter (https://github.com/rsms/inter), SIL Open Font License 1.1; see `src/Aero.App/ThirdParty/Inter/OFL.txt`.
- Material Symbols Rounded (https://github.com/google/material-design-icons), Apache License 2.0; see `src/Aero.App/ThirdParty/MaterialSymbols/LICENSE.txt`. `AeroExpressiveIcons.ttf` and `AeroGlassIcons.ttf` are modified subsets with glyphs remapped to new code points.

Aero is not currently a Chromium fork and does not ship its own independent HTML, CSS, or JavaScript engine.
