# SeoTester's Chromium container (docs/seotester/README.md "The Chromium container"; wired into compose in M12).
# The Playwright image of EXACTLY the Microsoft.Playwright version SeoTester references (1.63.0): ConnectAsync needs
# the same major.minor on both sides. playwright-core is installed here, so the container needs no network at start
# (the seo-browser network is meant to be internal: no egress at all).
FROM mcr.microsoft.com/playwright:v1.63.0-noble
WORKDIR /srv
RUN npm init -y > /dev/null && npm install --omit=dev --no-audit --no-fund playwright-core@1.63.0
COPY server.mjs ./
USER pwuser
EXPOSE 3000
CMD ["node", "server.mjs"]
