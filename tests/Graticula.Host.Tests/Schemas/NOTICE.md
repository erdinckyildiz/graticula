# Third-party XML Schemas — test data only

These files are copies of public XML Schemas, kept so that `OgcStylesTests` can validate the SLD documents this
server writes **without a network** (ADR-176 condition 4). They are test data: nothing in the server reads them, and
they are not part of any published image.

Fetched 2026-10-13 by following every `xs:import` and `xs:include` from:

- `http://schemas.opengis.net/sld/1.0.0/StyledLayerDescriptor.xsd`
- `http://schemas.opengis.net/sld/1.1.0/StyledLayerDescriptor.xsd`

Each file is stored under the host and path it was fetched from (`schemas.opengis.net/…`, `www.w3.org/…`), and
**no file has been modified**: every copyright header and notice is as published.

## schemas.opengis.net — OGC Software License 1.0

Copyright © Open Geospatial Consortium, Inc. All Rights Reserved.
<https://www.ogc.org/about-ogc/policies/copyright-notice-and-disclaimers/>

Each schema's own header names its years. The licence below is the one the headers point to
("To obtain additional rights of use, visit http://www.opengeospatial.org/legal/"), as published at
<https://www.ogc.org/about-ogc/policies/software-licenses/ogc-software-license-1-0/>:

> This OGC work (including software, documents, or other related items) is being provided by the copyright holders
> under the following license. By obtaining, using and/or copying this work, you (the licensee) agree that you have
> read, understood, and will comply with the following terms and conditions:
>
> Permission to use, copy, and modify this software and its documentation, with or without modification, for any
> purpose and without fee or royalty is hereby granted, provided that you include the following on ALL copies of the
> software and documentation or portions thereof, including modifications, that you make:
>
> 1. The full text of this NOTICE in a location viewable to users of the redistributed or derivative work.
> 2. Any pre-existing intellectual property disclaimers, notices, or terms and conditions. If none exist, a short
>    notice of the following form (hypertext is preferred, text is permitted) should be used within the body of any
>    redistributed or derivative code: "Copyright © [$date-of-document] Open Geospatial Consortium, Inc. All Rights
>    Reserved. https://www.ogc.org/about-ogc/policies/copyright-notice-and-disclaimers/" (Hypertext is preferred, but
>    a textual representation is permitted.)
> 3. Notice of any changes or modifications to the OGC files, including the date changes were made. (We recommend
>    you provide URIs to the location from which the code is derived.)
>
> THIS SOFTWARE AND DOCUMENTATION IS PROVIDED "AS IS," AND COPYRIGHT HOLDERS MAKE NO REPRESENTATIONS OR WARRANTIES,
> EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO, WARRANTIES OF MERCHANTABILITY OR FITNESS FOR ANY PARTICULAR
> PURPOSE OR THAT THE USE OF THE SOFTWARE OR DOCUMENTATION WILL NOT INFRINGE ANY THIRD PARTY PATENTS, COPYRIGHTS,
> TRADEMARKS OR OTHER RIGHTS.
>
> COPYRIGHT HOLDERS WILL NOT BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL OR CONSEQUENTIAL DAMAGES ARISING OUT OF ANY
> USE OF THE SOFTWARE OR DOCUMENTATION.
>
> The name and trademarks of copyright holders may NOT be used in advertising or publicity pertaining to the
> software without specific, written prior permission. Title to copyright in this software and any associated
> documentation will at all times remain with copyright holders.

## www.w3.org — W3C Software and Document License (2023)

`xlink.xsd` (Copyright 1998–2001 W3C (MIT, INRIA, Keio), All Rights Reserved) and `xml.xsd`, as their headers state,
under the licence published at <https://www.w3.org/copyright/software-license-2023/>:

> By obtaining and/or copying this work, you (the licensee) agree that you have read, understood, and will comply
> with the following terms and conditions.
>
> Permission to copy, modify, and distribute this work, with or without modification, for any purpose and without
> fee or royalty is hereby granted, provided that you include the following on ALL copies of the work or portions
> thereof, including modifications:
>
> - The full text of this NOTICE in a location viewable to users of the redistributed or derivative work.
> - Any pre-existing intellectual property disclaimers, notices, or terms and conditions. If none exist, the W3C
>   software and document short notice should be included.
> - Notice of any changes or modifications, through a copyright statement on the new code or document such as "This
>   software or document includes material copied from or derived from [title and URI of the W3C document].
>   Copyright © [$year-of-document] World Wide Web Consortium. https://www.w3.org/copyright/software-license-2023/"
>
> THIS WORK IS PROVIDED "AS IS," AND COPYRIGHT HOLDERS MAKE NO REPRESENTATIONS OR WARRANTIES, EXPRESS OR IMPLIED,
> INCLUDING BUT NOT LIMITED TO, WARRANTIES OF MERCHANTABILITY OR FITNESS FOR ANY PARTICULAR PURPOSE OR THAT THE USE
> OF THE SOFTWARE OR DOCUMENT WILL NOT INFRINGE ANY THIRD PARTY PATENTS, COPYRIGHTS, TRADEMARKS OR OTHER RIGHTS.
>
> COPYRIGHT HOLDERS WILL NOT BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL OR CONSEQUENTIAL DAMAGES ARISING OUT OF ANY
> USE OF THE SOFTWARE OR DOCUMENT.
>
> The name and trademarks of copyright holders may NOT be used in advertising or publicity pertaining to the work
> without specific, written prior permission. Title to copyright in this work will at all times remain with
> copyright holders.
