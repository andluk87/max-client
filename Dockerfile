# Linux image with Wine for cross-building Windows installers.
FROM electronuserland/builder:wine
WORKDIR /project
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src ./src
ENV CSC_IDENTITY_AUTO_DISCOVERY=false
CMD ["npm", "run", "dist:win"]
