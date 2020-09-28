# MyS3

A simple tool for encrypting and syncing files to the Amazon cloud using S3.

## Why

Are you using services like Dropbox, Google Drive, Microsoft OneDrive, etc? Do you trust them not to look at your personal photos, read your documents, etc? If so, why? Don't you know that surveillance is big business, and that your data is the currency?

Nobody knows what the future brings: What kind of society will we be living in? What kind of government will we have to endure? Is it (still) going to be a free country? What kind of new criminal laws will be passed? ….. So what is your data going to tell about you, in the future, if everything is turned upside down? Are you suddenly a suspicious person that needs to be watched 24/7? Are you even fit to be left alone, drive a car, own a gun or raise children? Can you be trusted not to do something stupid when flying? …. Anything can be cherry picked out of context, twisted around, and used against you. Race, religion, political affiliation, you name it.

Remember, everything you upload is very likely to stay in the cloud forever. And if you think you're in control, it's only imaginary. Legal mumbo jumbo in a terms of service (ToS) agreement, or something like GDPR, does nothing to make your data unreadable. You should instead put your trust in technical implementations that make it impossible.

...

Read the rest of the pitch at <code>Docs/WhyUseMyS3.pdf</code>

If you're uploading downloaded content like wallpapers, music, software, etc. you don't need MyS3 or client-side encryption. You can just sync your folders directly to S3 buckets instead, using the AWS CLI. For upload something like <code>aws s3 sync <path-to-local-folder> s3://<name-of-bucket></code> works fine. For download you switch folder path with S3 bucket name and get <code>aws s3 sync <path-to-folder> s3://<name-of-bucket></code>.

Tip: Make a sync script that runs every hour or day.

## What

MyS3 is a tool that makes is possible to encrypt file data on the fly and upload to Simple Storage Service (S3), which is part of Amazon Web Services (AWS).

Anyone can register an AWS account and create S3 buckets for file uploads. But enabling hassle free and secure file encryption is an entirely different matter. MyS3 does this for you after you setup your own AWS resources.

MyS3 is built using NET Core. It can be copied or imported into any kind of NET project.

See <code>Docs/UserManual.pdf</code> to get precise instructions with screenshots for complete AWS and MyS3 setup.

### Client Features

- Constantly monitoring for file changes.
- Uses symmetric encryption (AES-128 GCM) to encrypt file and it's file path.
- Supports use of multiple AWS S3 buckets at the same time.
- Can be run from multiple computers at the same time.
- Has two types of clients. A graphical interface client (Windows only) and a console client.

IMPORTANT: *Yes, MyS3 can run on multiple computers at the same time and share the same S3 bucket. But MyS3 doesn't support merging of files with identical file paths. It creates new S3 object versions instead. So whoever saves his file last "wins". Avoid this by not writing to the same file path from different locations at the same time. Each person using a shared bucket should put his files in his own unique folder. (And if your file still gets overwritten just restore it.)*

### Getting Started

The GUI client:
1. Download the ZIP file with the GUI client. (This is <code>MyS3.GUI.win-x64.zip</code>)
2. Unzip it's contents and move the entire folder to <code>C:\Program Files</code>.
3. Open the folder and double click the EXE file.
4. Right click on the MyS3 icon down at the taskbar and select to pin it.

The console client (CLI client):
1. Download the ZIP-file with the CLI client for your operating system. (For Windows this is <code>MyS3.CLI.win-x64.zip</code>)
2. Unzip the content and move the entire folder to where you want it.
3. Open a console window / a terminal and change directory to the MyS3's executable.
4. Type and run <code>MyS3.CLI.exe</code> on Windows or <code>./MyS3.CLI</code> on Linux.
5. Consider putting your command line arguments in a BAT or SH file when everything works OK.

## License

Has a regular MIT license.
