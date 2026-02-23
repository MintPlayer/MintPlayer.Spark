import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { TranslateKeyPipe } from '../../core/pipes/translate-key.pipe';

@Component({
  selector: 'app-home',
  imports: [CommonModule, TranslateKeyPipe],
  templateUrl: './home.component.html'
})
export default class HomeComponent {}
