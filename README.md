# CS2 City Assistant

A city advisor for Cities: Skylines II that runs in the Claude app. Claude reads your city's numbers straight from the game and tells you how the city is doing, what is going wrong and where on the map, and what to do next. On request it can also change a few numbers for you (taxes, service budgets, fees, the loan, city policies), always one at a time and only after you say yes.

It has three parts:

| Part | Folder | What it does |
|---|---|---|
| Game mod | `CityAssistant/` | Exports the city's numbers and map layers four times per in-game month and answers live requests from Claude while the game runs. |
| Connector (MCP server) | `CityAssistantMCP/` | Gives Claude tools over that data: live numbers, history, maps, worst places, layout plans, and the co-pilot changes. |
| Claude skill | `.claude/skills/cs2-city-assistant/` | Teaches Claude how to read the data, rate each part of the city, write reports and keep a journal per city. |

---

## Install guide (Claude app on Windows)

You need:

- **Cities: Skylines II** on Windows (built and checked with game version 1.6.1).
- The **Claude desktop app** for Windows ([claude.ai/download](https://claude.ai/download)), signed in.
- **Python 3.12 or newer** (checked with 3.13), from [python.org](https://www.python.org/downloads/).
- About 15 minutes.

### Step 1: Download the files

1. Open the **Releases** page of this repository (right-hand side of the GitHub page) and download from the newest release:
   - `CityAssistant-mod.zip` (the game mod)
   - `cs2-city-assistant.skill` (the Claude skill)
2. Download this repository itself: green **Code** button → **Download ZIP**.
3. Unzip the repository to a short path without spaces, for example `C:\CityAssistant`. The rest of this guide uses that path. If you pick another one, change it in every step below.

### Step 2: Install the game mod

1. Open this folder in File Explorer (paste it into the address bar):
   ```
   %USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods
   ```
   If there is no `Mods` folder, create it.
2. Unzip `CityAssistant-mod.zip` there. You should end up with `...\Mods\CityAssistant\CityAssistant.dll`.
3. Start the game once and load a city. The mod creates its data folder:
   ```
   %USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\ModsData\CityAssistant
   ```
   Exports only happen while the game is **unpaused**, so let it run for a minute. A folder with your city's name should appear under `cities\`.

### Step 3: Install Python and the connector

1. Install Python from [python.org](https://www.python.org/downloads/). In the first installer screen tick **"Add python.exe to PATH"**.
2. Open **PowerShell** (Start menu → type `PowerShell`) and run these three lines one by one:
   ```powershell
   cd C:\CityAssistant\CityAssistantMCP
   python -m venv .venv
   .\.venv\Scripts\pip install -r requirements.txt
   ```
   The last one downloads a few packages and takes a minute or two.
3. Test it (optional): this should print the cities the mod has exported so far.
   ```powershell
   .\.venv\Scripts\python.exe -c "import server; print(server.list_cities())"
   ```

### Step 4: Connect it to the Claude app

1. In the Claude app open **Settings → Developer → Edit Config**. This opens the folder with `claude_desktop_config.json`; open that file in Notepad.
2. Put this in it. If the file already has an `"mcpServers"` section, add only the `"city-assistant"` part inside it:
   ```json
   {
     "mcpServers": {
       "city-assistant": {
         "command": "C:\\CityAssistant\\CityAssistantMCP\\.venv\\Scripts\\python.exe",
         "args": ["C:\\CityAssistant\\CityAssistantMCP\\server.py"]
       }
     }
   }
   ```
   Paths in this file need double backslashes (`\\`).
3. Save, then **quit the Claude app completely** (right-click its icon in the system tray → Quit) and start it again.
4. Check: in a new chat, the tools menu (the sliders icon under the message box) should list **city-assistant**.

### Step 5: Add the skill

1. In the Claude app open **Settings → Capabilities** and make sure **Code execution and file creation** is on (skills need it).
2. In the **Skills** section choose **Upload skill** and pick `cs2-city-assistant.skill`. If the file picker does not accept it, rename the file to `cs2-city-assistant.zip` and upload that.
3. Make sure the skill is switched on.

### Step 6: First use

1. Start the game, load your city and unpause it for a minute.
2. In the Claude app start a new chat and ask, for example:
   - *"How is my city doing?"*: a full report, every topic rated fine / watch / problem, the worst problems first, with places named by street and district.
   - *"Let's set up a charter for my city Felburg"*: a short interview about what kind of city you want (money, growth, style). Claude writes it into the city's journal and judges the city against it from then on. Recommended for every new city.
   - *"Where is the noise coming from?"*, *"Which tile should I buy next?"*, *"Who is using Elk Highway?"*, *"Where should I zone offices?"*
3. The first time Claude uses a tool, the app asks for permission. Reading tools are safe to allow. The tool **apply_change** changes your game: choose **allow once** every time, never "always allow", so you see each change before it happens.

Claude also works with the game closed. It then uses the latest export and the saved history, but it cannot read anything live or change anything.

---

## How co-pilot changes stay safe

- Only taxes, service budgets, fees, the loan and city-wide policies can be changed. Roads, zoning and buildings always stay with you.
- One setting per change. Claude first proposes it (shows *from → to*, why, and what to expect), and changes nothing until you say yes to that one change. Then the Claude app asks you once more (the tool permission).
- The mod checks every change against the game's own limits and refuses it if the value moved since the proposal.
- The mod only listens on your own PC (`127.0.0.1:51842`), needs a random key it creates at each game start, and refuses requests from web pages.
- Every change is written to the city's journal and to `actions.jsonl` in the data folder.

## Where your data is

`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\ModsData\CityAssistant\cities\<city name>\`

- the latest export of each topic (`overview.json`, `finance.json`, ...)
- `history\`: snapshots at 00:00, 06:00, 12:00 and 18:00 of every in-game month
- `journal.md`: Claude's notes for this city (charter, decisions and how they turned out)

Cities are kept apart by name. Two saves with the same city name share one folder.

## Troubleshooting

| Problem | What to do |
|---|---|
| No city folder appears in `ModsData\CityAssistant\cities` | The game must be unpaused for exports. Check that `Mods\CityAssistant\CityAssistant.dll` exists, and look at `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\CityAssistant.log`. |
| `city-assistant` is missing in the Claude app | Check the paths in `claude_desktop_config.json` (double backslashes) and that Step 3 finished without errors, then quit and restart the app. **Settings → Developer** shows the connector's status and log. |
| Claude says the game is not running | The mod answers only while a city is loaded. Claude then falls back to the last export. |
| Numbers look odd right after loading a save | The game re-counts everything after loading; the data is marked "warming up" for about 3 in-game hours. Let it run. |
| The game updated and something broke | The mod reads parts of the game that an update can change. Each part fails on its own (it shows as an error in that topic), the rest keeps working. |

The decompiled game code used for research is not part of this repository.
