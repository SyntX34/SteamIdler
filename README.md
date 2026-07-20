<p align="center">
  <img src="https://img.shields.io/badge/Steam-Idler-1b2838?style=for-the-badge&logo=steam&logoColor=white" alt="Steam Idler" />
  <img src="https://img.shields.io/badge/Version-2.2-4caf50?style=for-the-badge" alt="Version 2.2" />
  <img src="https://img.shields.io/badge/License-MIT-ff6b35?style=for-the-badge" alt="MIT License" />
  <img src="https://img.shields.io/badge/.NET-8.0-512bd4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 8.0" />
</p>

<h1 align="center">
  Steam Game Idler
</h1>

<p align="center">
  <em>A lightweight, memory-efficient tool to automatically idle your Steam games and collect hours, cards, and achievements — built with C# and SteamKit2.</em>
</p>

<p align="center">
  <b>by SyntX</b>
</p>

---

<h2>Features</h2>

<table>
  <tr>
    <td width="50%">
      <h3>🎮 Multi-Game Idling</h3>
      <p>Idle multiple Steam games simultaneously. Just list their AppIDs and let the tool handle the rest.</p>
    </td>
    <td width="50%">
      <h3>⏱️ Smart Cooldown</h3>
      <p>Configurable idle limits (default: 10 hours) with automatic cooldown breaks (default: 30 minutes) to prevent infinite sessions.</p>
    </td>
  </tr>
  <tr>
    <td width="50%">
      <h3>🔐 Persistent Sessions</h3>
      <p>Log in once. Steam refresh tokens are saved securely and reused — no need to re-enter your password or Steam Guard codes for weeks.</p>
    </td>
    <td width="50%">
      <h3>🔄 Auto-Reconnect</h3>
      <p>Automatically reconnects on network interruptions with exponential backoff (up to 5 attempts). No memory leaks, no infinite loops.</p>
    </td>
  </tr>
  <tr>
    <td width="50%">
      <h3>🏆 Achievement Tracking</h3>
      <p>Fetches and displays game names and achievement progress every 30 minutes. Track your collection in real-time.</p>
    </td>
    <td width="50%">
      <h3>💬 Chat & Auto-Reply</h3>
      <p>Receive chat messages, send replies, and configure automatic responses while idling. Chat log is saved to a text file.</p>
    </td>
  </tr>
  <tr>
    <td width="50%">
      <h3>🖥️ Dual Interface</h3>
      <p>Choose between a full-featured graphical interface (Windows Forms) or a lightweight CLI version.</p>
    </td>
    <td width="50%">
      <h3>📊 Live Statistics</h3>
      <p>Track idle duration per game with session statistics displayed every hour. Monitor your progress at a glance.</p>
    </td>
  </tr>
</table>

---

<h2>Screenshots</h2>

<p align="center">
  <em>Screenshots coming soon</em>
</p>

---

<h2>Getting Started</h2>

<h3>Prerequisites</h3>

<ul>
  <li><a href="https://dotnet.microsoft.com/download/dotnet/8.0">.NET 8.0 SDK</a> or later</li>
  <li>A Steam account with games in your library</li>
</ul>

<h3>Download</h3>

<p>Grab the latest release from the <a href="https://github.com/SyntX34/SteamIdler/releases">Releases</a> page. Pre-built binaries are available for Windows x64.</p>

<h3>Build from Source</h3>

<pre>
<code>git clone https://github.com/SyntX34/SteamIdler.git
cd SteamIdler/SteamIdler

# Build CLI version
dotnet build CLI/SteamIdler.csproj -c Release

# Build GUI version
dotnet build GUI/SteamIdlerGUI.csproj -c Release</code>
</pre>

<h3>Publish Standalone Executable</h3>

<pre>
<code>dotnet publish CLI/SteamIdler.csproj -c Release -r win-x64 --self-contained true -o ./publish/cli
dotnet publish GUI/SteamIdlerGUI.csproj -c Release -r win-x64 --self-contained true -o ./publish/gui</code>
</pre>

---

<h2>Configuration</h2>

<p>On first launch, a <code>config.json</code> file is created automatically. Edit it with your Steam credentials:</p>

<pre>
<code>{
  "username": "your_steam_username",
  "password": "your_steam_password",
  "refresh_token": "",
  "auto_reply_message": "Hey! I'm currently idling Steam games and can't chat right now.",
  "auto_reply_enabled": true,
  "max_idle_hours": 10.0,
  "cooldown_minutes": 30.0
}</code>
</pre>

<h3>Configuration Options</h3>

<table>
  <tr>
    <th>Setting</th>
    <th>Default</th>
    <th>Description</th>
  </tr>
  <tr>
    <td><code>username</code></td>
    <td><code>""</code></td>
    <td>Your Steam account username</td>
  </tr>
  <tr>
    <td><code>password</code></td>
    <td><code>""</code></td>
    <td>Your Steam account password</td>
  </tr>
  <tr>
    <td><code>refresh_token</code></td>
    <td><code>""</code></td>
    <td>Automatically saved after first login for persistent sessions</td>
  </tr>
  <tr>
    <td><code>auto_reply_message</code></td>
    <td><code>"Hey! I'm currently idling..."</code></td>
    <td>Auto-reply message sent to friends who message you</td>
  </tr>
  <tr>
    <td><code>auto_reply_enabled</code></td>
    <td><code>true</code></td>
    <td>Enable or disable automatic chat replies</td>
  </tr>
  <tr>
    <td><code>max_idle_hours</code></td>
    <td><code>10.0</code></td>
    <td>Maximum consecutive idle hours before a cooldown is triggered</td>
  </tr>
  <tr>
    <td><code>cooldown_minutes</code></td>
    <td><code>30.0</code></td>
    <td>Cooldown duration in minutes after reaching max idle hours</td>
  </tr>
</table>

---

<h2>Usage</h2>

<h3>Games List</h3>

<p>Create a <code>games.txt</code> file in the same folder as the executable. Add one Steam AppID per line. Lines starting with <code>//</code> are treated as comments.</p>

<pre>
<code>730     // Counter-Strike 2
570     // Dota 2
440     // Team Fortress 2
252950  // Rocket League</code>
</pre>

<h3>CLI Commands</h3>

<p>While the CLI version is running, you can use these commands:</p>

<table>
  <tr>
    <th>Command</th>
    <th>Description</th>
  </tr>
  <tr>
    <td><code>quit</code></td>
    <td>Stop idling and exit the program</td>
  </tr>
  <tr>
    <td><code>friends</code></td>
    <td>Display your Steam friends list with names, IDs, and statuses</td>
  </tr>
  <tr>
    <td><code>msg &lt;SteamID64&gt; &lt;message&gt;</code></td>
    <td>Send a direct chat message to a friend by their 64-bit Steam ID</td>
  </tr>
</table>

<h3>GUI Features</h3>

<ul>
  <li><strong>Dashboard</strong> — Connect, stop, manage game list, and view a live log of all activity</li>
  <li><strong>Friends</strong> — Browse your Steam friends list and double-click to start a chat</li>
  <li><strong>Chat</strong> — Send and receive messages in real-time</li>
  <li><strong>Settings</strong> — Update your credentials, auto-reply message, and manage saved session tokens</li>
</ul>

---

<h2>Memory & CPU Efficiency</h2>

<p>Steam Game Idler is designed to be lightweight:</p>

<ul>
  <li><strong>Single callback pump</strong> — Only one Steam client callback loop runs at any time</li>
  <li><strong>Guarded reconnection</strong> — Prevents cascading reconnect tasks that could consume gigabytes of memory</li>
  <li><strong>Max 5 reconnect attempts</strong> — Stops retrying after 5 failed attempts with exponential backoff</li>
  <li><strong>Throttled logging</strong> — Repeated disconnect/connect messages are rate-limited to prevent log spam</li>
  <li><strong>Proper resource cleanup</strong> — Old SteamClient objects and callback pumps are fully disposed before creating new ones</li>
</ul>

---

<h2>Session Persistence</h2>

<p>Steam's authentication system supports long-lived refresh tokens. When you log in with <code>IsPersistentSession = true</code>, the tool saves your refresh token to <code>config.json</code>. On subsequent launches, you can idle immediately without entering a password or Steam Guard code — the token is valid for weeks or longer.</p>

<p>If a token expires or is revoked, the tool automatically falls back to password-based authentication and saves a fresh token.</p>

---

<h2>Building</h2>

<table>
  <tr>
    <th>Project</th>
    <th>Target</th>
    <th>Command</th>
  </tr>
  <tr>
    <td>CLI</td>
    <td><code>net8.0</code></td>
    <td><code>dotnet build CLI/SteamIdler.csproj -c Release</code></td>
  </tr>
  <tr>
    <td>GUI</td>
    <td><code>net8.0-windows</code></td>
    <td><code>dotnet build GUI/SteamIdlerGUI.csproj -c Release</code></td>
  </tr>
</table>

<h3>Dependencies</h3>

<ul>
  <li><a href="https://github.com/SteamRE/SteamKit">SteamKit2</a> — .NET Steam protocol library</li>
  <li><a href="https://www.newtonsoft.com/json">Newtonsoft.Json</a> — JSON serialization</li>
</ul>

---

<h2>License</h2>

<p>This project is open source under the <strong>MIT License</strong>. See the <a href="LICENSE">LICENSE</a> file for details.</p>

---

<h2>Disclaimer</h2>

<p>This tool is not affiliated with Valve Corporation or Steam. Use at your own risk. Idling Steam games for card drops and hours is allowed under Steam's terms of service, but automated interactions may be subject to Steam's API usage policies.</p>

---

<p align="center">
  <sub>Built with ❤️ by <a href="https://github.com/SyntX34">SyntX</a></sub>
  <br>
  <sub>Powered by <a href="https://github.com/SteamRE/SteamKit">SteamKit2</a></sub>
</p>
