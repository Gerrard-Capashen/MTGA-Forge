# Project website

Static one-page site (`public/index.html`), served by Cloudflare Workers static assets.

    cd site
    npx wrangler dev        # preview locally
    npx wrangler deploy     # publish to mtgaforge.com (and play.mtga-forge.workers.dev, kept for older launchers)

The "Download: coming soon" button in `public/index.html` is a placeholder until the package is published.
