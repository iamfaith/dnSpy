﻿/*
    Copyright (C) 2014-2016 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using dnSpy.Contracts.Settings.Groups;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace dnSpy.Text.Groups {
	sealed class TextViewOptionsGroup : ITextViewOptionsGroup {
		IEnumerable<IWpfTextView> ITextViewOptionsGroup.TextViews {
			get { return textViews.ToArray(); }
		}

		readonly List<IWpfTextView> textViews;
		readonly Dictionary<IContentType, TextViewGroupOptionCollection> toOptions;
		readonly IContentTypeRegistryService contentTypeRegistryService;
		readonly OptionsStorage optionsStorage;
		readonly string groupName;

		public TextViewOptionsGroup(string groupName, IContentTypeRegistryService contentTypeRegistryService, ContentTypeOptionDefinition[] defaultOptions, OptionsStorage optionsStorage) {
			if (groupName == null)
				throw new ArgumentNullException(nameof(groupName));
			if (contentTypeRegistryService == null)
				throw new ArgumentNullException(nameof(contentTypeRegistryService));
			if (defaultOptions == null)
				throw new ArgumentNullException(nameof(defaultOptions));
			if (optionsStorage == null)
				throw new ArgumentNullException(nameof(optionsStorage));
			this.contentTypeRegistryService = contentTypeRegistryService;
			this.textViews = new List<IWpfTextView>();
			this.toOptions = new Dictionary<IContentType, TextViewGroupOptionCollection>();
			this.groupName = groupName;

			foreach (var option in defaultOptions) {
				Debug.Assert(option.Name != null);
				if (option.Name == null)
					continue;

				var ct = option.ContentType == null ? null : contentTypeRegistryService.GetContentType(option.ContentType);
				Debug.Assert(ct != null);
				if (ct == null)
					continue;

				TextViewGroupOptionCollection coll;
				if (!toOptions.TryGetValue(ct, out coll))
					toOptions.Add(ct, coll = new TextViewGroupOptionCollection(ct));
				coll.Add(new TextViewGroupOption(this, option));
			}

			foreach (var coll in toOptions.Values)
				optionsStorage.InitializeOptions(groupName, coll);
			this.optionsStorage = optionsStorage;
		}

		TextViewGroupOptionCollection TryGetCollection(string contentType) => TryGetCollection(contentTypeRegistryService.GetContentType(contentType));
		TextViewGroupOptionCollection TryGetCollection(IContentType contentType) {
			if (contentType == null)
				return null;
			TextViewGroupOptionCollection coll;
			toOptions.TryGetValue(contentType, out coll);
			return coll;
		}

		public bool HasOption<T>(string contentType, EditorOptionKey<T> option) => HasOption(contentType, option.Name);
		public bool HasOption(string contentType, string optionId) {
			if (contentType == null)
				throw new ArgumentNullException(nameof(contentType));
			if (optionId == null)
				throw new ArgumentNullException(nameof(optionId));
			var coll = TryGetCollection(contentType);
			if (coll == null)
				return false;
			return coll.HasOption(optionId);
		}

		public T GetOptionValue<T>(string contentType, EditorOptionKey<T> option) => (T)GetOptionValue(contentType, option.Name);
		public object GetOptionValue(string contentType, string optionId) {
			if (contentType == null)
				throw new ArgumentNullException(nameof(contentType));
			if (optionId == null)
				throw new ArgumentNullException(nameof(optionId));
			var coll = TryGetCollection(contentType);
			if (coll == null)
				throw new ArgumentException($"Invalid content type: {contentType}", nameof(contentType));
			return coll.GetOptionValue(optionId);
		}

		public void SetOptionValue<T>(string contentType, EditorOptionKey<T> option, T value) => SetOptionValue(contentType, option.Name, value);
		public void SetOptionValue(string contentType, string optionId, object value) {
			if (contentType == null)
				throw new ArgumentNullException(nameof(contentType));
			if (optionId == null)
				throw new ArgumentNullException(nameof(optionId));
			var coll = TryGetCollection(contentType);
			if (coll == null)
				throw new ArgumentException($"Invalid content type: {contentType}", nameof(contentType));
			coll.SetOptionValue(optionId, value);
		}

		public void TextViewCreated(IWpfTextView textView) {
			if (textView == null)
				throw new ArgumentNullException(nameof(textView));
			Debug.Assert(!textView.IsClosed);
			if (textView.IsClosed)
				return;
			textViews.Add(textView);
			new TextViewListener(this, textView);
		}

		sealed class TextViewListener {
			readonly TextViewOptionsGroup owner;
			readonly IWpfTextView textView;

			public TextViewListener(TextViewOptionsGroup owner, IWpfTextView textView) {
				this.owner = owner;
				this.textView = textView;
				textView.Closed += TextView_Closed;
				textView.Options.OptionChanged += Options_OptionChanged;
				textView.TextDataModel.ContentTypeChanged += TextDataModel_ContentTypeChanged;
				owner.InitializeOptions(textView, null, textView.TextDataModel.ContentType);
			}

			void Options_OptionChanged(object sender, EditorOptionChangedEventArgs e) {
				if (textView.IsClosed)
					return;
				owner.OptionChanged(textView, e);
			}

			void TextDataModel_ContentTypeChanged(object sender, TextDataModelContentTypeChangedEventArgs e) {
				if (textView.IsClosed)
					return;
				owner.InitializeOptions(textView, e.BeforeContentType, e.AfterContentType);
			}

			void TextView_Closed(object sender, EventArgs e) {
				textView.Closed -= TextView_Closed;
				textView.Options.OptionChanged -= Options_OptionChanged;
				textView.TextDataModel.ContentTypeChanged -= TextDataModel_ContentTypeChanged;
				owner.Closed(textView);
			}
		}

		readonly HashSet<TextViewGroupOption> writeOptionHash = new HashSet<TextViewGroupOption>();
		public void OptionChanged(TextViewGroupOption option) {
			if (optionsStorage == null)
				return;
			if (writeOptionHash.Contains(option))
				return;
			try {
				writeOptionHash.Add(option);
				optionsStorage.Write(groupName, option);
				foreach (var textView in textViews.ToArray())
					textView.Options.SetOptionValue(option.OptionId, option.Value);
			}
			finally {
				writeOptionHash.Remove(option);
			}
		}

		void OptionChanged(IWpfTextView textView, EditorOptionChangedEventArgs e) {
			var coll = TryGetCollection(textView.TextDataModel.ContentType);
			if (coll == null)
				return;
			if (!coll.HasOption(e.OptionId))
				return;
			coll.SetOptionValue(e.OptionId, textView.Options.GetOptionValue(e.OptionId));
		}

		void InitializeOptions(IWpfTextView textView, IContentType beforeContentType, IContentType afterContentType) {
			var oldColl = TryGetCollection(beforeContentType);
			var newColl = TryGetCollection(afterContentType);
			if (oldColl == newColl)
				return;
			if (newColl == null)
				return;

			newColl.InitializeOptions(textView);
		}

		void Closed(IWpfTextView textView) {
			Debug.Assert(textView.IsClosed);
			bool b = textViews.Remove(textView);
			Debug.Assert(b);
		}
	}
}