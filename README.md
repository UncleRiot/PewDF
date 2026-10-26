# 🔥 PewDF

### A portable PDF editor for Windows — independently developed from KillerPDF

**PewDF** is a free and open-source PDF editor for Windows, built on the foundation of [KillerPDF](https://github.com/SteveTheKiller/KillerPDF).

What started as a fork has evolved into a separate development project with its own identity, a streamlined application structure, customized user interface, and a growing collection of usability improvements.

The goal is simple: **a powerful, practical, and portable PDF editor without subscriptions, unnecessary installation requirements, or unnecessary complexity.**

![Platform](https://img.shields.io/badge/Platform-Windows_x64-blue)
![.NET](https://img.shields.io/badge/.NET-10.0-purple)
![License](https://img.shields.io/badge/License-GPLv3-green)
![Status](https://img.shields.io/badge/Status-Active_Development-orange)
![Portable](https://img.shields.io/badge/Deployment-Portable-brightgreen)

> [!IMPORTANT]
> **PewDF is an independent fork of KillerPDF.**
>
> - **Original project:** [KillerPDF](https://github.com/SteveTheKiller/KillerPDF)
> - **Original author:** [SteveTheKiller](https://github.com/SteveTheKiller)
> - **PewDF development:** [UncleRiot](https://github.com/UncleRiot)
> - **KillerPDF base version:** 1.8.91
> - **License:** [GNU GPL v3.0](LICENSE)
>
> PewDF is not affiliated with or officially maintained by the original KillerPDF author.

---

<img width="1515" height="1122" alt="grafik" src="https://github.com/user-attachments/assets/817185de-b693-463a-8537-9c64f9f9b309" />

---

## ✨ Why PewDF?

PewDF retains the powerful PDF-processing foundation of KillerPDF while taking a different approach to application design, distribution, and everyday usability.

Rather than maintaining every component of the original project, PewDF focuses on a streamlined Windows application and its core PDF editing experience.

### 🚀 Key differences from KillerPDF

| Area | KillerPDF | PewDF |
|---|---|---|
| **Project identity** | Original KillerPDF application | Independently branded PewDF application |
| **Distribution focus** | Installer and portable editions | Portable-first, without the original installer infrastructure |
| **Repository structure** | Extensive monorepo with packaging, automation, tests, and documentation | Streamlined repository focused on the application and PDF engine |
| **Application branding** | KillerPDF executable, icons, and application identity | Dedicated PewDF executable, icons, and application identity |
| **User interface** | Original KillerPDF interface | Independently customized interface and tool layouts |
| **Window integration** | Original window implementation | Native Windows title bar integration |
| **Editing tools** | Original editing workflows | Customized editing workflows and additional UI refinements |
| **PDF engine** | KillerPDF.Engine | KillerPDF.Engine retained as the technical foundation |
| **Development direction** | Original upstream roadmap | Independently maintained and developed |

### 🧩 Redesigned application experience

PewDF introduces application-level modifications intended to make everyday PDF work more comfortable and predictable.

Development areas include:

- **Reorganized editing controls** — revised layouts and tool placement
- **Improved tool panels** — more consistent spacing, alignment, and interaction
- **Text editing refinements** — ongoing improvements to caret behavior and editing interaction
- **Highlighting tools** — custom text-marker handling, cursor presentation, and rendering refinements
- **Ribbon customization** — ongoing work on tool organization and configurable controls
- **Window behavior** — native Windows title bar integration and customized application presentation

These modifications represent PewDF's own development direction. Some remain under active development and may change.

### 📦 Portable-first approach

PewDF is designed around a portable Windows application rather than the original multi-format distribution infrastructure.

This approach aims to provide:

- No traditional installer requirement
- No separate installer or uninstaller workflow
- A simpler application-oriented project structure
- A dedicated PewDF executable
- Easier manual deployment and replacement of application files

> [!NOTE]
> Portable does not necessarily mean that the application writes no data to the system. Configuration, caches, or runtime resources may still be stored in Windows user directories.

### 🛠️ Independent maintenance

PewDF has its own application project, executable identity, resources, and development priorities.

The original PDF engine is retained, while the surrounding Windows application can evolve independently.

This separation allows PewDF to focus on user-interface improvements and application workflows without unnecessarily replacing the underlying PDF-processing technology.

---

## 🧰 PDF Features

PewDF builds upon the extensive functionality of KillerPDF.

The following capabilities originate from the shared project foundation and are not presented as exclusive PewDF additions.

### 📖 Viewing & Navigation

- Open and view PDF documents
- Multiple document tabs
- Single-page, continuous, two-page, and grid viewing modes
- Side-by-side document viewing
- Zoom, navigation, and full-text search
- Open password-protected PDF files

### ✏️ Editing & Annotation

- Edit and add text
- Add text boxes and annotations
- Draw and highlight document content
- Insert images and stamps
- Add signatures
- Undo and redo editing operations

### 📄 Page Management

- Merge and split PDF documents
- Insert, extract, and delete pages
- Rotate and crop pages
- Reorder pages
- Export pages as images

### 🔍 OCR & Document Processing

- Optical character recognition using Tesseract
- Convert scanned documents into searchable PDFs
- Extract text from PDF documents
- Compare PDF documents
- Transform and correct document pages
- Flatten PDF content

### 🖨️ Forms & Printing

- Fill supported PDF forms
- Work with digital signatures
- Print PDF documents
- Configure print layout and page selection

### 🎨 Customization

- Multiple application themes
- Accent color customization
- Multilingual interface
- Configurable viewing and editing options

> [!NOTE]
> PewDF is under active development. Individual features may be incomplete, modified, or temporarily unavailable in development builds.

---

## 🏗️ Technology

PewDF uses the following technologies:

| Component | Technology |
|---|---|
| Programming language | C# |
| Framework | .NET 10 |
| Desktop UI | Windows Presentation Foundation (WPF) |
| Architecture | Windows x64 |
| PDF engine | KillerPDF.Engine |
| PDF rendering | PDFium-based rendering |
| OCR | Tesseract |
| License | GNU GPL v3.0 |

The underlying **KillerPDF.Engine** continues to provide the core PDF-processing functionality.

PewDF's independent development primarily concerns the application layer, interaction design, and surrounding functionality.

---

## 📥 Installation & Availability

> [!WARNING]
> **PewDF is currently under active development.**
>
> No stable PewDF release is available yet. The repository contains development source code, and functionality may change.

### Build from source

The project is developed using C# and .NET 10 for Windows x64.

To work with the source:

1. Clone or download this repository.
2. Open `PewDF.sln` in a compatible version of Visual Studio.
3. Restore the required NuGet dependencies.
4. Build the application for Windows x64.

The main application project is `PewDF.csproj`.

A successful build does not necessarily indicate that every feature has been validated for production use.

---

## 🗺️ Development Priorities

Current areas of development include:

- [ ] Further UI and ribbon refinements
- [ ] Consistent tool-panel layouts
- [ ] Text editing and caret behavior improvements
- [ ] Highlighting quality and rendering performance
- [ ] Smoother PDF navigation and window interaction
- [ ] Application settings and Windows integration
- [ ] Stability testing and regression verification
- [ ] Preparation of a stable portable release

This list reflects development priorities rather than a guaranteed feature or release schedule.

---

## 🔄 Relationship to KillerPDF

PewDF exists because of the work originally done on KillerPDF.

The original project provides the PDF engine, much of the application foundation, and numerous document-processing capabilities.

PewDF builds on that work rather than claiming to have independently created those features.

**What distinguishes PewDF is the continued independent development of the application itself.**

The intention is not simply to rename KillerPDF, but to shape a separate PDF editor with its own user experience, distribution approach, and development priorities.

PewDF is an independent community fork, not an official successor or replacement for KillerPDF.

For the original project, visit:

**[SteveTheKiller / KillerPDF](https://github.com/SteveTheKiller/KillerPDF)**

---

## 🙏 Credits & Acknowledgments

Special thanks to **[SteveTheKiller](https://github.com/SteveTheKiller)** for creating KillerPDF and making its source code publicly available.

Without that foundation, PewDF would not exist in its current form.

The original author's contribution is explicitly acknowledged, and the technical foundation remains credited to KillerPDF.

**Project credits:**

- **Original application and foundation:** SteveTheKiller
- **Original PDF engine:** KillerPDF.Engine
- **PewDF fork and independent development:** UncleRiot

All applicable original copyright notices and third-party license requirements remain in effect.

---

## ⚖️ License

PewDF is distributed under the **GNU General Public License version 3 (GPLv3)**.

As a derivative of KillerPDF, PewDF remains subject to the applicable open-source licensing requirements.

See the [LICENSE](LICENSE) file for the complete license terms.

---

**PewDF — built on KillerPDF, developed in its own direction.**
