# Unreleased

Notes accumulate here as changes land, and this file is renamed to the version when a release
goes out. The dated files beside it are history and are never edited.

## The site has a mark of its own

`stubid.dev` shipped with docfx's stock icon in the tab and the header. It now carries one drawn
for the project: an ID card as a dashed outline around a person, which is the placeholder
convention doing the work the name does — identity-shaped, and openly not the real thing. Nothing
about it approximates any broker's logo, which the non-affiliation notice in `README.md` makes a
requirement rather than a preference.

The tab icon is an `.ico` carrying 16, 32 and 48 pixel cuts rather than a scaled vector, because
a dashed outline is exactly the shape a browser downscales badly; the header takes the vector.
Both sit at the site root, where they displace the two files the template would otherwise put
there, so a browser that probes `/favicon.ico` without reading the tag gets this one too.
