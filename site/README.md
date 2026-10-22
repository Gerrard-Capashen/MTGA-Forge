# Project website

Static one-page site (`public/index.html`), served by Cloudflare Workers static assets.

    cd site
    npx wrangler dev        # preview locally
    npx wrangler deploy     # publish to play.mtga-forge.workers.dev; add a custom domain in the dashboard

The "Download: coming soon" button in `public/index.html` is a placeholder until the package is published.
