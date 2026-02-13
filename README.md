# Instagram Media Downloader

<div align="center">
  
[![Status](https://img.shields.io/badge/status-active-success.svg)]()
[![Docker Pulls](https://img.shields.io/docker/pulls/taljamri/ig-media-downloader.svg)](https://hub.docker.com/r/taljamri/ig-media-downloader)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](/LICENSE)

</div>

An Instagram chat bot that enables users to download posts (photos/videos) and stories effortlessly via Instagram Direct Messages.

## 📥 How to Use
*Activation: Send any text message to the bot's account **(only for the first time)** **[@download.this.pls](https://www.instagram.com/download.this.pls)** for activation*
<p>There are **two ways** to download media using the bot:
</p>

### Method 1: Share via Direct Message
1. Open Instagram and find the post/story you want to download
2. Tap the share button (paper plane icon)
3. Send it to the bot's account **[@download.this.pls](https://www.instagram.com/download.this.pls)**
4. The bot will automatically send the media back to you for download

### Method 2: Mention the Bot
1. Find any public post you want to download
2. Comment and mention **[@download.this.pls](https://www.instagram.com/download.this.pls)** on the post
3. The bot will send you the media via Direct Message

https://github.com/user-attachments/assets/1e61017f-3970-40aa-83c0-5d17e007265e

> **Note:** When using the mention method, please allow some time for the bot to respond, especially after recent activation. Instagram's API can introduce delays in processing mentions and notifications.


---

## 🚀 Deploy Your Own Bot Instance

Want to run your own private Instagram media downloader bot? Follow these steps to deploy using Docker.

### Prerequisites
- Docker installed on your system
- An Instagram account dedicated for the bot
- Basic command line knowledge

### Step 1: Pull the Docker Image
```bash
docker pull taljamri/ig-media-downloader:latest
```

### Step 2: Create a Persistent Data Folder
This folder stores the SQLite database and runtime files to ensure data persists between container restarts.

```bash
sudo mkdir -p /opt/ig-media-downloader/data
sudo chown -R $USER:$USER /opt/ig-media-downloader
```

### Step 3: Run the Container

⚠️ **Important:** You must provide your bot's Instagram credentials via environment variables.

**Basic deployment:**
```bash
docker run -d \
  --name ig-media-downloader \
  --restart unless-stopped \
  -v /opt/ig-media-downloader/data:/data \
  -e USERNAME="YOUR_IG_BOT_USERNAME" \
  -e PASSWORD="YOUR_IG_BOT_PASSWORD" \
  taljamri/ig-media-downloader:latest
```

**Advanced deployment with custom polling intervals:**
```bash
docker run -d \
  --name ig-media-downloader \
  --restart unless-stopped \
  -v /opt/ig-media-downloader/data:/data \
  -e USERNAME="YOUR_BOT_USERNAME" \
  -e PASSWORD="YOUR_BOT_PASSWORD" \
  -e POLL_MSGS_DELAY_MS=20000 \
  -e POLL_REQS_DELAY_MS=80000 \
  taljamri/ig-media-downloader:latest
```

### Step 4: Monitor the Bot

View live logs to ensure the bot is running correctly:
```bash
docker logs -f ig-media-downloader
```

To stop following logs, press `Ctrl + C`.

---

## ⚙️ Configuration Options

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `USERNAME` | *Required* | Instagram username for the bot account |
| `PASSWORD` | *Required* | Instagram password for the bot account |
| `POLL_MSGS_DELAY_MS` | 30000 | Delay between message polling cycles (milliseconds) |
| `FAIL_POLL_MSGS_DELAY_MS` | 1800000 | Delay after failed message polling (milliseconds) |
| `POLL_REQS_DELAY_MS` | 100000 | Delay between request polling cycles (milliseconds) |
| `FAIL_POLL_REQS_DELAY_MS` | 1800000 | Delay after failed request polling (milliseconds) |

### Volume Mounts

- `/data` - Persistent storage for database and runtime files
  - **Important:** Always mount this volume to prevent data loss on container restart

---

## 🔧 Troubleshooting

### Data Resets After Restart
If your bot loses data after restarting, ensure the volume is correctly mounted:
```bash
-v /opt/ig-media-downloader/data:/data
```

Verify the volume is mounted:
```bash
docker inspect ig-media-downloader | grep Mounts -A 10
```

### Bot Not Responding
1. Check if the container is running: `docker ps`
2. View logs for errors: `docker logs ig-media-downloader`
3. Verify Instagram credentials are correct
4. Ensure the bot account hasn't been rate-limited or blocked by Instagram

### Slow Response Times
- Instagram's API may introduce delays, especially for mention-based downloads
- Consider adjusting polling intervals via environment variables
- Recent activations may experience longer delays as the bot synchronizes with Instagram

---

## 📄 License
This project is licensed under MIT license.
