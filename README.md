# Instagram-MediaDownloader
An Instagram chat bot that enables users to download posts (photos/videos) and stories effortlessly via Instagram Direct Messages

## How to use:
<p>Send any photo/video to the bot, and it will send it back to you to download it in you device</p>
<img src="bot.gif" alt="Watch the gif" width="215" height="410">

## Download and Run using Docker

### 1) Pull the Docker image
```bash
docker pull taljamri/ig-media-downloader:latest
```

### 2) Create a persistent data folder (recommended)

This folder stores the SQLite database and any persistent runtime files.
```bash
sudo mkdir -p /opt/ig-media-downloader/data
sudo chown -R $USER:$USER /opt/ig-media-downloader
```

### 3) Run the container

⚠️ **Required:** You must provide the bot's Instagram account credentials via environment variables:
`USERNAME` and `PASSWORD`.
```bash
docker run -d \
  --name ig-media-downloader \
  -v /opt/ig-media-downloader/data:/data \
  -e USERNAME="YOUR_BOT_USERNAME" \
  -e PASSWORD="YOUR_BOT_PASSWORD" \
  taljamri/ig-media-downloader:latest
```
#### Optional Env Variables (polling delays)

- `POLL_MSGS_DELAY_MS` (default: 30000)
- `FAIL_POLL_MSGS_DELAY_MS` (default: 1800000)
- `POLL_REQS_DELAY_MS` (default: 100000)
- `FAIL_POLL_REQS_DELAY_MS` (default: 1800000)


### 4) View logs
```bash
docker logs -f ig-media-downloader
```

### 5) Running the Docker container:
```bash
docker run -d \
  --name ig-media-downloader \
  --restart unless-stopped \
  -v /opt/ig-media-downloader/data:/data \
  -e USERNAME="YOUR_BOT_USERNAME" \
  -e PASSWORD="YOUR_BOT_PASSWORD" \
  -e POLL_MSGS_DELAY_MS=20000 \
  taljamri/ig-media-downloader:latest
```

### 6) Data resets after restart

- Make sure you mounted `/data` correctly:
```bash
  -v /opt/ig-media-downloader/data:/data
```

## 7) 📄 License

This project is licensed under MIT license.
