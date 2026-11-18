# Messages module (Unity)

Team chat on the desk: a chat panel in MSG mode, a "New for you" list beside it, and the arrival digest + Albie's
Send / Cancel card that keep working while MSG mode is closed. Hub side: `hub/messaging/` (Socket Mode, no tunnel);
spec: `docs/PROJECT_CONTEXT.md` 7, 8.7 and 8.8; event shapes mirrored in `Core/Net/ForgeEvents.cs` (`NotifyMessage`,
`ChatChannel` / `NotifyChannels`, `NotifyDraft`, `NotifySent`, `NotifyDraftCancelled`, `NotifyDigest`).
With the hub live it shows the real workspace (messenger tokens in `hub/.env`, hub/README.md). With the hub off (and
always on a standalone headset: the cloud has no `/messages`) it runs on the bundled **sample workspace**,
`Resources/MessagesDemo.json` via `MessagesDemo`: "Garage Lab (sample)", four fictional teammates (Maya Chen, Jordan
Lee, Sam Ortiz, Priya Nair), `#general` and one channel per starter project (`#rc-truck`, `#quadcopter`, `#rocket`,
`#image-classifier`, each tagged with its `"project"` id), whose numbers match the notebook and CAD/ML mocks. The frame
says "sample", sends stay on the device, and one scripted message arrives only after you open Messages or switch
project, about the project open then.

## What it does

- **Chat panel** (`ChatPanel.prefab`, panel id `messages`, slot Main): a conversation sidebar and a message pane in
  graphite with the MSG violet. Sidebar: the workspace's name, **Channels** A-Z (`#`, or a lock for private ones) and
  **Direct messages** (an initials avatar; a group DM shows how many people), unread ones bold, a count pill for unread
  DMs and for mentions, the open one lit. Conversation: a rounded dark pane with the header (`#` name or the person,
  topic), the messages oldest -> newest as chat rows (`ChatRow`: rounded-square avatar, bold name, "4:15 PM", the
  text; follow-ups from the same person within 5 minutes grouped under one name, their time in the gutter on hover),
  day dividers ("Today", "Yesterday", "Wednesday, October 22nd"), a red **New** line above what was unread when the
  conversation opened, "N replies" in link blue, "replied in a thread" on a reply, "(edited)". Message markup renders this
  way: `<@U..>` as @name (yours yellow-highlighted, others blue), `#channel` and links blue, `*bold*`,
  `_italic_`, `~strike~`, inline code and code blocks. Opening a conversation GETs its history (cached per conversation
  for the session) and POSTs `/messages/channels/{id}/seen`; new messages in the open conversation are appended and
  marked seen; the list follows new messages unless you scrolled up, and always follows your own. Point at a message: it
  lights up and **Ask Albie** floats at its top right (`messages.focus`).
- **Composer**, the message box: a rounded field with a grey edge ("Message #general"), **Ask Albie** inside it on
  the left and the **Send** arrow on the right (green once there is text). What you type becomes a draft
  (`POST /messages/drafts {channel, text}`) that is sent at once (`POST .../send`), since you wrote it; the message shows
  in the conversation as soon as the hub answers with its ts (`ChatStore.AddOwn`), and the hub's `notify.message` for it
  (and `notify.sent`) are no-ops then. A draft from Albie (`notify.draft`) lands in the field as editable text, the panel
  switches to its conversation, and a banner above the box says "**Albie drafted this** for #x. Edit it, then Send." with
  **Cancel**; Send `PATCH`es the text first when you changed it (`ChatNet.Patch`, see Notes), Cancel
  `POST`s `.../cancel`. The composer also closes the draft on `notify.sent` / `notify.draft_cancelled`, so "send it" to Albie
  works. **Ask Albie** sends `messages.focus` for the conversation's latest message (Albie reads it aloud and offers to answer).
  On the Quest the field opens the system keyboard when poked; on desktop the keyboard types into it (Enter sends).
- **Mentions panel** (`MentionsPanel.prefab`, panel id `messages-mentions`, slot Left). "New for you": unread DMs and
  mentions across conversations as cards, newest first, 8 at most plus "+N more". Tapping a card opens that conversation
  in the chat panel; the card clears once the conversation is seen. Replaces the old `MessagesPanel`.
- **Arrival digest.** The pet's `ArrivalDirector` POSTs `/messages/digest` once per sit (`DigestTrigger.RequestDigest()`);
  the hub answers with `notify.digest` and a `pet.say` line Albie speaks. With MSG mode closed, a floating `MentionsPanel`
  (`floating = true`) shows the digest's cards (4 at most, "+N more") beside Albie at the desk's front-right and folds after
  20 s, when MSG mode opens, or when you leave the desk. Tapping a card opens MSG mode on that conversation.
- **Send / Cancel card** (`DraftCard.prefab`). With the chat panel closed, `notify.draft` pops a `DraftCard` beside Albie
  (or in front of the camera without a desk): recipient, what it answers, the exact text, Send and Cancel. Sent, it says
  "Sent to #x" in green for ~2 s before it folds; the message is in that conversation when you open it. With the panel
  open, `DraftPresenter` hands the draft to the composer instead (and a floating card moves into the composer when the
  panel opens). A draft with files (`NotifyDraft.attachments`, e.g. a CAD design's STEP / STL / drawing / parts list) shows
  them as one line on the card and on the composer's "Albie drafted this" banner; the hub uploads them with the text on Send.
- **Albie opens a conversation** ("open my chat with Adrian", "pull up #general": the hub's `messages_open` tool, or
  `POST /messages/open {channel}`). `ChatRemote` (self-installing, like `DraftPresenter`) takes the hub's
  `messages.open`, shows that conversation on the chat panel
  (`ChatPanel.ForChannel`), opening MSG mode when it is closed (like tapping a Mentions
  card), and answers `messages.opened {ok, reason?}` in the same frame (`not_at_desk` away from the desk), so Albie says
  what really happened. A DM the hub just opened gets a row at once (`ChatStore.Provisional`).

- **Projects <-> messages** (`MessagesShare`). `MessagesShare.DraftForProject(projectId, out status)` drafts a message
  to the project's channel with its latest real result (newest run, CAD design version and verdict, ML epoch and
  validation accuracy, else the notebook's last judged attempt). It is only a draft: the composer or the DraftCard shows
  it with Send / Cancel, as for Albie's `messages_send`. A project channel's header has **Open project** (the same
  `OSProjects.OpenProject` path as `os_open`), and `messages_read_channel` / `messages_open` return
  `project: {id, title, open_with}` so Albie can offer it.

## Look

"Graphite + Albie". Every colour and size is in `Scripts/MessagesLook.cs`, to tune after a headset test.

- Frame: Hudson's graphite `ForgePanel` (64 mm header, MSG badge, title, subtitle; Content insets 18 / 16 / 78 mm). Inside,
  the chat panel keeps its own colours (`ChatUi.Msg*`: violet-cast graphite #201F28, pane #15161A, links #C7A8FF, count
  pills #A77BFF, New line #FF7A6B). Subtext is 62% white (Core's is 50%).
- Type: Fredoka (SIL OFL 1.1), the Pet module's dynamic TMP font (`AlbieCaption.DefaultFont`, Pet/Resources/PetFonts).
  It is made at runtime, so prefabs are saved with Core's Inter and each panel switches to Fredoka in Awake
  (`ChatUi.ApplyFontAll`). Body text 14 mm. One weight (Medium); bold is TMP faux bold, tightened (`ChatUi.SetBold`).
- Albie is talking = a white sticker, ink border, lime (#C8DC5A) hard shadow (`ChatUi.AlbieSticker`): his summary line
  from the last digest above the chat panel, the draft on the Send / Cancel card, the composer's "Albie drafted this".
- A mention of you = lime: a lime "@you" sticker tag on Mentions cards, a lime mark with ink text in the conversation.
- Orange (#FA4616) fills Send only (ink text, 5.0:1); it is close to the CAD hue, so it is never a mark.
- Stickers are flat `ForgeSurface` fills (shadow, border, fill): hard offsets, no blur.
- Albie's picture (`Resources/AlbiePortrait.png`, his render, 1024 x 750, transparent) peeks over the chat panel's top
  edge beside his summary sticker, and sits at the top right of the draft card. Drawn by a RawImage at his own aspect
  (`MessagesLook.PortraitAspect`); it never takes the pointer, and with the texture missing nothing is drawn.
- Check it: **Forge > Messages > Check Messages** (after Build Prefabs).

## Files

| File | What |
|---|---|
| `Scripts/ChatPanel.cs` | The chat panel: sidebar, conversation (dividers, New line, rows), composer; `ChatPanel.Active`, `Open(channelId)`, `FocusAlbie`. UI built at runtime under Content. |
| `Scripts/ChatRow.cs` | One message as a chat row (avatar, name + time, text, replies; grouped follow-ups; hover + Ask Albie). Built in code, pooled. |
| `Scripts/ChatRemote.cs` | Albie's `messages.open`: opens MSG mode on a conversation, answers `messages.opened`. Self-installs. |
| `Resources/ChatIcons/*.svg` | send, lock, caret, sparkle (white, imported as sprites). |
| `Scripts/ChatComposer.cs` | The composer (field, Send / Ask Albie / Cancel, draft state, the OnUpdateSelected drive for typing). |
| `Scripts/MentionsPanel.cs` | "New for you" + the floating digest copy. |
| `Scripts/MessageCard.cs` | One message on a slate card (`MessageCard.prefab`): Inbox and Thread styles, `Measure` / `Layout`, hover, click. |
| `Scripts/ChatStore.cs` | Static model: channels (`notify.channels`, GET), per-conversation history cache, user names, "me", the workspace's name, recent messages; `MarkSeen`, `LoadHistory`, `AddOwn` (a sent message, at once), `Provisional`. |
| `Scripts/ChatData.cs` | REST DTOs (`ChatHistory`, `ChatMe`, `ChatOpenRequest`, ...), `ChatText` (ts maths, clock times and day labels, initials, one '#' per channel name, message markup -> TMP rich text in the accent or the panel's look), `ChatNet.Patch`. |
| `Scripts/MessagesLook.cs` | The look's constants (colours, type scale, sticker and Albie sizes), the font, Albie's picture, contrast maths. |
| `Scripts/ChatUi.cs`, `ChatButton.cs`, `ChatHitArea.cs` | Colours, type scale and factories (cards, pills, scroll lists, stickers, Albie's picture), the hover button, the invisible hit area. |
| `Editor/MessagesCheck.cs` | **Forge > Messages > Check Messages**: PASS / FAIL per step (one provider, wire tolerance, contrast, Send once, sticker, Albie, font, brand words). |
| `Scripts/ForgeInput.cs` | `ForgeInput.TextFieldFocused`: true while the composer has the keyboard (for DesktopRig / pet key handlers to check). |
| `Scripts/DigestTrigger.cs` | `RequestDigest()` + the floating digest; shared `SpawnAtDesk` / `FaceUser` / `PopIn` / `FoldOut` (the Pet module calls them). |
| `Scripts/DraftPresenter.cs` | Routes `notify.draft`: composer when the chat panel is open, else a floating `DraftCard`. Self-installs. |
| `Scripts/DraftCard.cs` | The Send / Cancel card. |
| `Scripts/MessagesDemo.cs`, `Resources/MessagesDemo.json` | Hub off: the sample workspace, its drafts and sends on the device, the one scripted message. |
| `Scripts/MessagesTools.cs` | Albie's messaging tools over the sample workspace (hub off). |
| `Scripts/MessagesShare.cs` | Project <-> channel links and `DraftForProject` (a draft with the project's latest result). |
| `Scripts/MessagesDeskZone.cs` | Marker + desk-frame `Footprint` the pet keeps clear of while cards float (unchanged). |
| `Editor/MessagesPrefabBuilder.cs` | **Forge > Messages > Build Prefabs** builds the four prefabs below (and deletes the old MessagesPanel.prefab). |
| `Resources/ChatPanel.prefab`, `MentionsPanel.prefab`, `MessageCard.prefab`, `DraftCard.prefab` | Built, not hand-edited; commit them with their `.meta`s. |

## Slotting it into Main (Hudson)

`Workspace.DefaultModes()` has the `messages` mode with `("messages", "Messages", Main)` and `("messages-mentions", "Mentions", Left)`;
`MainSceneBuilder.ModulePrefabs` maps them to `Resources/ChatPanel.prefab` and `Resources/MentionsPanel.prefab`.
Run **Forge > Messages > Build Prefabs**, then **Forge > Build Main Scene**. `DigestTrigger` and `DraftPresenter` need
nothing in the scene: they add themselves at load when a `DeskPresence` exists.

## Test live

1. `hub/`: tokens in `.env`, `uvicorn main:app --host 0.0.0.0 --port 8000`.
2. Unity: **Forge > Messages > Build Prefabs** once (and after script changes to the prefabs), **Forge > Build Main Scene**. Play `Main`.
3. Sit at the desk (desktop: `E`). Albie says the digest line; the floating "New for you" cards appear beside him. Tap one: MSG
   mode opens on that conversation. Or press **MSG** on the dock.
4. Click conversations on the left; the unread pill clears. Scroll the list by dragging it (mouse or hand ray); "Load
   earlier" at the top when there is more. Type in the field, Enter or **Send**: the message appears in the workspace under your
   name and comes back as your own (tinted) row.
5. From your phone, post in a channel or DM Albie: the row lands at the bottom, the Mentions panel lists it, the pill counts.
6. Say to Albie "reply that I'm on it": his draft lands in the composer with Cancel; edit it, Send: it shows in the
   conversation right away. **Ask Albie** reads the newest message aloud. Close MSG mode and ask for another reply: the
   Send / Cancel card floats beside Albie instead; Send says "Sent to ...", and the message is there when you open MSG.
7. "Albie, open my chat with Adrian" (or `POST /messages/open {"channel": "Adrian"}` on the hub): MSG mode opens on that
   DM. Standing away from the desk, Albie says to sit down first.

## Notes

- One provider only. The laptop hub can still emit items tagged for a provider the app dropped (its `source` tag, or
  `im:` conversation ids): `ChatStore.Unsupported` drops them where they arrive (channel list, messages, digest,
  history, own sends), `DraftPresenter` and the panel ignore such drafts, and `ChatRemote` leaves such a
  `messages.open` unanswered (the hub's wait times out). No warning, no row, no card.

- Everything goes through `ForgeNet` (events + REST) except `PATCH /messages/drafts/{id}`, which ForgeNet lacks:
  `ChatNet.Patch` runs the same `UnityWebRequest` shape on `ForgeNet.Url(path)` from the ForgeNet instance.
- Typing on desktop: Main's only input module is the Interaction SDK's `PointableCanvasModule`, which never sends
  `OnUpdateSelected` (where TMP reads key events), so `ChatComposer.Tick` sends it while the field is focused.
- `ForgeInput.TextFieldFocused` is set while the field has focus. DesktopRig (WASD, Shift, E) and the pet's key handlers
  don't check it yet: flagged for Core / Pet.
- Quest keyboard: TMP_InputField opens `TouchScreenKeyboard` itself; the Meta project config needs "Requires System
  Keyboard" on (flagged for Core).
- No `Update` work beyond the composer's focus check: lists rebuild only when data or the panel size changes, rows and
  cards are pooled, rendered rich text is cached per message, relative times refresh every 30 s.
- First import generates `.meta` files for the new scripts and prefabs: commit them with the prefabs.
