# Downhole Studio — Quick Start and Activation

This guide gets Downhole Studio installed, activated and showing results in about fifteen minutes. The complete user guide, the field guide and the training video are in the same client package.

## 1. What you need

| Item | Requirement |
|---|---|
| Computer | 64-bit Windows 10 (21H2 or later) or Windows 11, 8 GB RAM recommended |
| Disk space | About 200 MB for the program, plus room for your projects |
| Internet | Not needed: installation, activation and analysis all work offline |
| Administrator rights | Not needed: the program installs for the current user by default |
| Field data | SEG-2 records from a downhole survey (example records are in the client package) |

## 2. Install

1. Open the folder **1 - Install Downhole Studio** in the client package and double-click `DownholeStudio-Setup-1.0.0.exe`. You can also download the installer directly from the product page.
2. If Windows shows *Windows protected your PC* or *Unknown publisher*, click **More info**, then **Run anyway**.
3. Accept the licence agreement. Optionally create a desktop shortcut and open `.dhsx` project files with Downhole Studio.
4. Leave **Launch Downhole Studio** selected and click **Finish**.

> Installing a newer version over an older one keeps your projects, settings and activation. Uninstalling never removes your projects.

<!-- pagebreak -->

## 3. Activate (no internet needed)

Each licence is for one computer. Activation takes three steps.

1. **Find this computer's code.** When Downhole Studio starts for the first time, the activation window shows the *computer code*: five groups of five letters and digits, for example `7K3M9-QPZX2-AB4HV-N0R5T-W8YCD`. The code identifies this computer only. It contains no personal or project data.
2. **Send the code to QuakeLogic.** Click **E-mail to QuakeLogic**. This opens a pre-filled e-mail to sales@quakelogic.net. Add your company name and order number, then send it. On a computer without e-mail, click **Copy Code** and send the code from another device.
3. **Enter the activation key.** QuakeLogic replies with an *activation key* for this computer. It is a long line of text that starts with `dhs1.`, and it also comes as a `.dhslic` file attached to the e-mail. Either paste the key into the activation window and click **Activate**, or click **Open Activation File…** and choose the `.dhslic` file.

The window then shows **Licensed**, with your company name. The activation is stored on this computer and needs no further checks.

> Typing the code by hand? The letters O and I are read as the digits 0 and 1, and a check character catches typing errors.

| Message | What to do |
|---|---|
| *This activation key was issued for another computer* | The key belongs to a different computer code. The message shows this computer's code: send it to QuakeLogic. |
| *This activation key is not valid* | Part of the key is missing. Copy the whole key, or use **Open Activation File…** instead. |
| *This activation key expired* or *License expired* | The licence had an end date. Contact QuakeLogic to renew it. Your projects can still be opened. |

**Moving to a new computer.** On the old computer, open **Settings → License** and click **Remove Activation from This Computer**. Then send the new computer's code to QuakeLogic. A computer code changes when Windows is reinstalled or the computer is replaced; reinstalling or updating Downhole Studio does not change it.

<!-- pagebreak -->

## 4. Your first fifteen minutes

1. On the start screen, click **Explore Sample Project**. This opens a finished synthetic survey with a known answer, so every page shows real content.
2. Use the **workflow strip** at the top to visit each page: Import, Geometry, Waveforms, Processing, P Picks, S Picks, Velocity Model, Engineering, Results and Report.
3. Press **F1** on any page for help about that page. **Guided** mode explains each step; **Expert** mode shows every parameter.
4. Watch the training video (folder **4 - Training Video**) to see the whole analysis once, with narration.

## 5. Your own survey

1. Click **New Project** and give the project a name and location.
2. On the **Import** page, add your SEG-2 files (or the example records in folder **8 - Example Field Records**). Every file is inspected before it is accepted.
3. On **Geometry**, enter the borehole collar, the P and S source offsets and the depth of each record.
4. Follow the orange **next step** button at the top of each page. It always points to what still needs attention.
5. On **Report**, choose the sections and export a PDF report, Excel tables and figures.

> Raw records are never modified. Processing, picks and layers are stored separately, and every change can be undone.

## 6. Support

| Need | Contact |
|---|---|
| Activation keys, licences and sales | sales@quakelogic.net · +1 (916) 899-0391 |
| Technical support | support@quakelogic.net (reply within 3 business days). In Downhole Studio, **Settings → Diagnostics → Export diagnostic package** collects the information support needs. |
| New versions and documents | https://github.com/omerozgul11/ql-downhole |
