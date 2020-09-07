# MyS3

A simple tool for encrypting and syncing files to the Amazon cloud using S3.

## Why

Are you using services like Dropbox, Google Drive, Microsoft OneDrive, etc? Do you trust them not to look at your personal photos, read your documents, etc? If so, why? Don't you know that surveillance is big business, and that your data is the currency?

Nobody knows what the future brings: What kind of society will we be living in? What kind of government will we have to endure? Is it (still) going to be a free country? What kind of new criminal laws will be passed? ….. So what is your data going to tell about you, in the future, if everything is turned upside down? Are you suddenly a suspicious person that needs to be watched 24/7? Are you even fit to be left alone, drive a car, own a gun or raise children? Can you be trusted not to do something stupid when flying? …. Anything can be cherry picked out of context, twisted around, and used against you. Race, religion, political affiliation, you name it.

Remember, everything you upload is very likely to stay in the cloud forever. And if you think you're in control, it's only imaginary. Legal mumbo jumbo in a terms of service (ToS) agreement, or something like GDPR, does nothing to make your data unreadable. You should instead put your trust in technical implementations that make it impossible.

...

Read the rest of the pitch at <code>Docs/WhyUseMyS3.pdf</code>

## What

MyS3 is a tool that makes is possible to encrypt file data on the fly and upload to Simple Storage Service (S3), which is part of Amazon Web Services (AWS).

Anyone can register an AWS account and create S3 buckets for file uploads. But enabling hassle free and secure file encryption is an entirely different matter. MyS3 does this for you after you setup your own AWS resources.

MyS3 is built using NET Core. It can be copied or imported into any kind of NET project.

## Client Features

- Constantly monitoring for file changes.
- Uses symmetric encryption (AES-128 GCM) to encrypt file and it's file path.
- Supports use of multiple AWS S3 buckets at the same time.
- Can be run from multiple computers at the same time.
- Has two types of clients. A graphical interface client (Windows only) and a console client.

See <code>Docs/UserManual.pdf</code> to get precise instructions with screenshots for complete AWS and MyS3 setup.

## License

Has a regular MIT license.
