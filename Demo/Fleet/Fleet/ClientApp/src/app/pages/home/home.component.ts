import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { BsCardModule } from '@mintplayer/ng-bootstrap/card';
import { BsGridModule } from '@mintplayer/ng-bootstrap/grid';
import { TranslateKeyPipe } from '../../core/pipes/translate-key.pipe';

@Component({
  selector: 'app-home',
  imports: [CommonModule, BsCardModule, BsGridModule, TranslateKeyPipe],
  templateUrl: './home.component.html'
})
export default class HomeComponent {}
